using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using Rampside.Core.Simulation;

namespace Rampside.MsfsBridge;

/// <summary>
/// Lists the SimObjects around the user aircraft every few seconds (SimConnect_RequestDataOnSimObjectType),
/// so Rampside can see equipment placed by the aircraft itself (PMDG stairs and GPU), GSX or the sim.
/// Runs on the bridge's message thread.
/// </summary>
public sealed class NearbyObjectScanner : ISimConnectExtension
{
    public const uint RadiusMeters = 80;
    private const int ScanEverySeconds = 2;

    private readonly object _gate = new();
    private readonly List<NearbyObject> _pending = new();
    private IReadOnlyList<NearbyObject> _published = Array.Empty<NearbyObject>();
    private SimConnect? _sc;
    private uint _userObjectId;
    private int _seconds;
    private uint _scan;
    private bool _answered = true;

    private enum Definition : uint
    {
        User = 0x4E42_0001,
        Object = 0x4E42_0002,
    }

    private enum Request : uint
    {
        User = 0x4E42_0001,

        // Scans use ScanBase + (counter % 256) so late answers to an old scan are recognised.
        ScanBase = 0x4E42_0100,
    }

    public IReadOnlyList<NearbyObject> Objects
    {
        get { lock (_gate) return _published; }
    }

    public void OnOpen(SimConnect sc)
    {
        _sc = sc;
        sc.AddToDataDefinition(Definition.User, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<UserData>(Definition.User);

        // Field order must match ObjectData.
        sc.AddToDataDefinition(Definition.Object, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToDataDefinition(Definition.Object, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToDataDefinition(Definition.Object, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToDataDefinition(Definition.Object, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<ObjectData>(Definition.Object);

        sc.OnRecvSimobjectData += OnUserData;
        sc.OnRecvSimobjectDataBytype += OnObjectData;

        // Once per second; also the clock for the scans.
        sc.RequestDataOnSimObject(Request.User, Definition.User, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    public void OnClientData(SIMCONNECT_RECV_CLIENT_DATA data)
    {
    }

    public void OnClose()
    {
        _sc = null;
        _userObjectId = 0;
        _pending.Clear();
        _answered = true;
        lock (_gate) _published = Array.Empty<NearbyObject>();
    }

    private void OnUserData(SimConnect sc, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID != (uint)Request.User)
        {
            return;
        }

        _userObjectId = data.dwObjectID;
        if (++_seconds % ScanEverySeconds != 0)
        {
            return;
        }

        // A scan that found nothing may not answer at all.
        if (!_answered)
        {
            Publish();
        }

        _scan = (_scan + 1) % 256;
        _pending.Clear();
        _answered = false;
        sc.RequestDataOnSimObjectType((Request)((uint)Request.ScanBase + _scan), Definition.Object, RadiusMeters, SIMCONNECT_SIMOBJECT_TYPE.ALL);
    }

    private void OnObjectData(SimConnect sc, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
    {
        if (data.dwRequestID != (uint)Request.ScanBase + _scan)
        {
            return;
        }

        if (data.dwoutof > 0 && data.dwObjectID != _userObjectId && data.dwData.Length > 0 && data.dwData[0] is ObjectData d)
        {
            _pending.Add(new NearbyObject(data.dwObjectID, d.Title ?? string.Empty, d.Latitude, d.Longitude, d.HeadingTrueDeg));
        }

        if (data.dwoutof == 0 || data.dwentrynumber >= data.dwoutof)
        {
            Publish();
        }
    }

    private void Publish()
    {
        _answered = true;
        var objects = _pending.ToArray();
        _pending.Clear();
        lock (_gate) _published = objects;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UserData
    {
        public int OnGround;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct ObjectData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
        public double Latitude;
        public double Longitude;
        public double HeadingTrueDeg;
    }
}
