using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using Rampside.Core.Simulation;

namespace Rampside.MsfsBridge;

/// <summary>
/// Finds the stand the user aircraft is parked on and watches its jetway:
/// nearest airport (facility list) → parking spots and taxiway graph (facility data, cached per airport)
/// → stand under the aircraft → jetway status once per second (SimConnect_RequestJetwayData, matched by parking index).
/// Runs entirely on the bridge's message thread.
/// </summary>
public sealed class StandMonitor : ISimConnectExtension
{
    private const double MaxAirportDistanceMeters = 8000;
    private const double RelocateMeters = 30;
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(10);

    // Facility data types (SIMCONNECT_FACILITY_DATA_TYPE in SimConnect.h):
    // AIRPORT = 0, TAXI_POINT = 14, TAXI_PARKING = 15, TAXI_PATH = 16, TAXI_NAME = 17.
    private const SIMCONNECT_FACILITY_DATA_TYPE AirportType = (SIMCONNECT_FACILITY_DATA_TYPE)0;
    private const SIMCONNECT_FACILITY_DATA_TYPE PointType = (SIMCONNECT_FACILITY_DATA_TYPE)14;
    private const SIMCONNECT_FACILITY_DATA_TYPE ParkingType = (SIMCONNECT_FACILITY_DATA_TYPE)15;
    private const SIMCONNECT_FACILITY_DATA_TYPE PathType = (SIMCONNECT_FACILITY_DATA_TYPE)16;
    private const SIMCONNECT_FACILITY_DATA_TYPE NameType = (SIMCONNECT_FACILITY_DATA_TYPE)17;

    private readonly object _gate = new();
    private readonly Dictionary<string, AirportLayout> _layoutCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ParkingSpot> _pendingParkings = new();
    private readonly SortedDictionary<int, TaxiPoint> _pendingPoints = new();
    private readonly List<TaxiPathData> _pendingPaths = new();
    private readonly SortedDictionary<int, string> _pendingNames = new();
    private SimConnect? _sc;
    private uint _userObjectId;
    private Position? _position;
    private DateTime _lastSearch = DateTime.MinValue;
    private readonly List<(string Ident, double Distance)> _airportCandidates = new();
    private bool _airportListPending;
    private string? _pendingAirport;
    private double _pendingAirportLat;
    private double _pendingAirportLon;
    private (string Icao, ParkingSpot Spot, double Lat, double Lon, TaxiwayNetwork Taxiways)? _stand;
    private StandInfo? _published;
    private volatile string? _airport;

    private enum Definition : uint
    {
        Position = 0x5354_0001,
        Parkings = 0x5354_0002,
    }

    private enum Request : uint
    {
        Position = 0x5354_0001,
        Airports = 0x5354_0002,
        Parkings = 0x5354_0003,
    }

    public event EventHandler<string>? Message;

    /// <summary>Current stand with jetway state, or null when not parked on a known stand.</summary>
    public StandInfo? Stand
    {
        get { lock (_gate) return _published; }
    }

    /// <summary>Nearest airport within 8 km while parked (also when not on a stand), or null.</summary>
    public string? Airport => Stand?.AirportIcao ?? _airport;

    /// <summary>Parking spots of <see cref="Airport"/> once its facility data is loaded, otherwise empty.</summary>
    public IReadOnlyList<ParkingSpot> Parkings => _airport is null ? Array.Empty<ParkingSpot>() : _parkings;

    private volatile IReadOnlyList<ParkingSpot> _parkings = Array.Empty<ParkingSpot>();

    public void OnOpen(SimConnect sc)
    {
        _sc = sc;
        sc.AddToDataDefinition(Definition.Position, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToDataDefinition(Definition.Position, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToDataDefinition(Definition.Position, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToDataDefinition(Definition.Position, "GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<Position>(Definition.Position);

        // Field order must match the structs below.
        foreach (var field in new[]
        {
            "OPEN AIRPORT", "LATITUDE", "LONGITUDE",
            "OPEN TAXI_PARKING", "NAME", "NUMBER", "SUFFIX", "HEADING", "RADIUS", "BIAS_X", "BIAS_Z", "CLOSE TAXI_PARKING",
            "OPEN TAXI_POINT", "BIAS_X", "BIAS_Z", "CLOSE TAXI_POINT",
            "OPEN TAXI_PATH", "TYPE", "WIDTH", "START", "END", "NAME_INDEX", "CLOSE TAXI_PATH",
            "OPEN TAXI_NAME", "NAME", "CLOSE TAXI_NAME",
            "CLOSE AIRPORT",
        })
        {
            sc.AddToFacilityDefinition(Definition.Parkings, field);
        }

        sc.RegisterFacilityDataDefineStruct<AirportData>(AirportType);
        sc.RegisterFacilityDataDefineStruct<ParkingData>(ParkingType);
        sc.RegisterFacilityDataDefineStruct<TaxiPointData>(PointType);
        sc.RegisterFacilityDataDefineStruct<TaxiPathData>(PathType);
        sc.RegisterFacilityDataDefineStruct<TaxiNameData>(NameType);

        sc.OnRecvSimobjectData += OnPosition;
        sc.OnRecvAirportList += OnAirportList;
        sc.OnRecvFacilityData += OnFacilityData;
        sc.OnRecvFacilityDataEnd += OnFacilityDataEnd;
        sc.OnRecvJetwayData += OnJetwayData;

        // Once per second: drives both the stand search and the jetway polling.
        sc.RequestDataOnSimObject(Request.Position, Definition.Position, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    public void OnClientData(SIMCONNECT_RECV_CLIENT_DATA data)
    {
    }

    public void OnClose()
    {
        _sc = null;
        _stand = null;
        _pendingAirport = null;
        _airportListPending = false;
        _airportCandidates.Clear();
        _position = null;
        _airport = null;
        lock (_gate) _published = null;
    }

    private void OnPosition(SimConnect sc, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID != (uint)Request.Position)
        {
            return;
        }

        var position = (Position)data.dwData[0];
        _position = position;
        _userObjectId = data.dwObjectID;

        var parked = position.OnGround != 0 && position.GroundSpeedKnots < 1;
        if (!parked)
        {
            ClearStand();
            _airport = null;
            return;
        }

        if (_stand is { } stand && StandLocator.DistanceMeters(position.Latitude, position.Longitude, stand.Lat, stand.Lon) <= stand.Spot.RadiusMeters + StandLocator.ToleranceMeters + RelocateMeters)
        {
            sc.RequestJetwayData(stand.Icao, new List<int>());
            return;
        }

        // A list that never completes (e.g. after a SimConnect exception) does not block the search for long.
        var listStuck = _airportListPending && DateTime.UtcNow - _lastSearch >= 3 * SearchInterval;
        if (_pendingAirport is null && (!_airportListPending || listStuck) && DateTime.UtcNow - _lastSearch >= SearchInterval)
        {
            _lastSearch = DateTime.UtcNow;
            _airportListPending = true;
            _airportCandidates.Clear();
            sc.RequestFacilitiesList(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT, Request.Airports);
        }
    }

    /// <summary>
    /// The airport list arrives in several messages (dwEntryNumber of dwOutOf); the nearest airport is chosen once the
    /// last one is in. Choosing per message picked whichever later message had any airport within range: at Frankfurt
    /// EDDF (1.7 km) came in message 45 of 76 and a heliport "SFDF8" (6.2 km) in message 75, so EDDF's stands were
    /// loaded under the heliport's ident and its jetways could not be read.
    /// </summary>
    private void OnAirportList(SimConnect sc, SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        if (data.dwRequestID != (uint)Request.Airports || _position is not { } position)
        {
            return;
        }

        if (data.dwEntryNumber == 0)
        {
            _airportCandidates.Clear();
        }

        _airportCandidates.AddRange(data.rgData
            .Cast<SIMCONNECT_DATA_FACILITY_AIRPORT>()
            .Select(a => (a.Ident, Distance: StandLocator.DistanceMeters(position.Latitude, position.Longitude, a.Latitude, a.Longitude)))
            .Where(a => !string.IsNullOrEmpty(a.Ident) && a.Distance <= MaxAirportDistanceMeters));

        if (data.dwEntryNumber + 1 < data.dwOutOf)
        {
            return; // more messages to come
        }

        _airportListPending = false;
        var nearest = _airportCandidates.OrderBy(a => a.Distance).FirstOrDefault();
        _airportCandidates.Clear();
        if (string.IsNullOrEmpty(nearest.Ident))
        {
            return; // no airport nearby
        }

        _airport = nearest.Ident;

        if (_layoutCache.TryGetValue(nearest.Ident, out var layout))
        {
            LocateStand(nearest.Ident, layout);
            return;
        }

        _pendingAirport = nearest.Ident;
        _parkings = Array.Empty<ParkingSpot>(); // not the previous airport's stands while this one loads
        _pendingParkings.Clear();
        _pendingPoints.Clear();
        _pendingPaths.Clear();
        _pendingNames.Clear();
        sc.RequestFacilityData(Definition.Parkings, Request.Parkings, nearest.Ident, string.Empty);
    }

    private void OnFacilityData(SimConnect sc, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (data.UserRequestId != (uint)Request.Parkings || _pendingAirport is null)
        {
            return;
        }

        if (data.Type == (uint)AirportType)
        {
            var airport = (AirportData)data.Data[0];
            _pendingAirportLat = airport.Latitude;
            _pendingAirportLon = airport.Longitude;
        }
        else if (data.Type == (uint)ParkingType)
        {
            var p = (ParkingData)data.Data[0];
            var (lat, lon) = StandLocator.Offset(_pendingAirportLat, _pendingAirportLon, p.BiasX, p.BiasZ);
            _pendingParkings.Add(new ParkingSpot((int)data.ItemIndex, p.Name, p.Number, p.Suffix, p.Heading, p.Radius, lat, lon));
        }
        else if (data.Type == (uint)PointType)
        {
            var t = (TaxiPointData)data.Data[0];
            var (lat, lon) = StandLocator.Offset(_pendingAirportLat, _pendingAirportLon, t.BiasX, t.BiasZ);
            _pendingPoints[(int)data.ItemIndex] = new TaxiPoint(lat, lon);
        }
        else if (data.Type == (uint)PathType)
        {
            _pendingPaths.Add((TaxiPathData)data.Data[0]);
        }
        else if (data.Type == (uint)NameType)
        {
            _pendingNames[(int)data.ItemIndex] = ((TaxiNameData)data.Data[0]).Name?.Trim() ?? string.Empty;
        }
    }

    private void OnFacilityDataEnd(SimConnect sc, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (data.RequestId != (uint)Request.Parkings || _pendingAirport is null)
        {
            return;
        }

        var icao = _pendingAirport;
        _pendingAirport = null;
        var layout = new AirportLayout(_pendingParkings.ToArray(), BuildTaxiways());
        _layoutCache[icao] = layout;
        Message?.Invoke(this, $"{icao}: {layout.Parkings.Count} parking spots, {layout.Taxiways.Points.Count} taxi points, {layout.Taxiways.Paths.Count} taxi paths loaded");
        LocateStand(icao, layout);
    }

    /// <summary>Points are indexed by their position in the facility list; gaps (should not happen) become unusable points.</summary>
    private TaxiwayNetwork BuildTaxiways()
    {
        var count = _pendingPoints.Count == 0 ? 0 : _pendingPoints.Keys.Max() + 1;
        var points = new TaxiPoint[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = _pendingPoints.TryGetValue(i, out var point) ? point : new TaxiPoint(double.NaN, double.NaN);
        }

        var paths = _pendingPaths
            .Select(p => new TaxiPath(
                Enum.IsDefined(typeof(TaxiPathType), p.Type) ? (TaxiPathType)p.Type : TaxiPathType.None,
                p.Start,
                p.End,
                _pendingNames.TryGetValue((int)p.NameIndex, out var name) ? name : string.Empty,
                p.Width))
            .ToArray();

        return new TaxiwayNetwork(points, paths);
    }

    private void LocateStand(string icao, AirportLayout layout)
    {
        var parkings = layout.Parkings;
        _parkings = parkings;
        if (_position is not { } position)
        {
            return;
        }

        var found = StandLocator.FindStand(parkings, position.Latitude, position.Longitude);
        if (found is null)
        {
            ClearStand();
            return;
        }

        var spot = found.Value.Spot;
        _stand = (icao, spot, spot.Latitude, spot.Longitude, layout.Taxiways);
        Message?.Invoke(this, $"Parked at {icao} {spot.DisplayName} ({found.Value.DistanceMeters:F1} m from its centre)");
        _sc?.RequestJetwayData(icao, new List<int>());
    }

    private void OnJetwayData(SimConnect sc, SIMCONNECT_RECV_JETWAY_DATA data)
    {
        if (_stand is not { } stand || _position is not { } position)
        {
            return;
        }

        var jetways = data.rgData.Cast<SIMCONNECT_JETWAY_DATA>()
            .Where(j => j.ParkingIndex == stand.Spot.Index && string.Equals(j.AirportIcao, stand.Icao, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A stand can have two jetways; prefer the one attached to us.
        JetwayInfo? info = null;
        if (jetways.Count > 0)
        {
            var jetway = jetways.OrderByDescending(j => j.AttachedObjectId == _userObjectId).First();
            info = new JetwayInfo(jetway.Status, _userObjectId != 0 && jetway.AttachedObjectId == _userObjectId, jetway.Door);
        }

        var distance = StandLocator.DistanceMeters(position.Latitude, position.Longitude, stand.Lat, stand.Lon);
        lock (_gate)
        {
            _published = new StandInfo(stand.Icao, stand.Spot.Index, stand.Spot.DisplayName, distance, stand.Spot.HeadingDeg, info, stand.Taxiways);
        }
    }

    private void ClearStand()
    {
        _stand = null;
        lock (_gate) _published = null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Position
    {
        public double Latitude;
        public double Longitude;
        public int OnGround;
        public double GroundSpeedKnots;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AirportData
    {
        public double Latitude;
        public double Longitude;
    }

    private sealed record AirportLayout(IReadOnlyList<ParkingSpot> Parkings, TaxiwayNetwork Taxiways);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaxiPointData
    {
        public float BiasX;
        public float BiasZ;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaxiPathData
    {
        public int Type;
        public float Width;
        public int Start;
        public int End;
        public uint NameIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TaxiNameData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ParkingData
    {
        public int Name;
        public uint Number;
        public int Suffix;
        public float Heading;
        public float Radius;
        public float BiasX;
        public float BiasZ;
    }
}
