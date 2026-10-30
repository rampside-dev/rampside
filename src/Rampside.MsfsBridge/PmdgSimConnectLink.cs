using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using Rampside.Core.Aircraft.Pmdg;

namespace Rampside.MsfsBridge;

/// <summary>
/// Reads a PMDG aircraft's SDK client data areas (aircraft state, CDU screens) and sends its control events,
/// using the layout and names from the PMDG header found on the user's machine.
/// </summary>
public sealed class PmdgSimConnectLink : IPmdgLink, ISimConnectExtension
{
    private const uint DataRequestId = 0x5244_0001;
    private const uint CduRequestBase = 0x5244_0010;

    private readonly PmdgAircraftSdk _sdk;
    private readonly PmdgSdkHeader _header;
    private readonly SimConnectBridge _bridge;
    private readonly PmdgStructLayout _dataLayout;
    private readonly PmdgStructLayout _cduLayout;
    private readonly object _gate = new();
    private readonly PmdgCduScreen?[] _cdus;
    private PmdgDataBlock? _data;

    public PmdgSimConnectLink(PmdgAircraftSdk sdk, PmdgSdkHeader header, SimConnectBridge bridge)
    {
        _sdk = sdk;
        _header = header;
        _bridge = bridge;
        _dataLayout = header.GetStruct(sdk.DataStruct);
        _cduLayout = header.GetStruct(sdk.CduStruct);
        _cdus = new PmdgCduScreen?[sdk.CduDefines.Count];

        foreach (var layout in new[] { _dataLayout, _cduLayout })
        {
            if (!ClientDataChunks.Supports(layout.Size))
            {
                throw new NotSupportedException($"{layout.Name} has {layout.Size} bytes; Rampside reads {ClientDataChunks.ChunkSize}..{ClientDataChunks.BufferSize}.");
            }
        }
    }

    private enum ClientDataId : uint
    {
    }

    private enum DefinitionId : uint
    {
    }

    private enum RequestId : uint
    {
    }

    public string HeaderPath => _header.SourcePath;
    public int DataSize => _dataLayout.Size;
    public DateTime? LastDataUtc { get; private set; }

    public bool HasData
    {
        get { lock (_gate) return _data is not null; }
    }

    public string Diagnostics
    {
        get
        {
            var age = LastDataUtc is { } t ? $"{(DateTime.UtcNow - t).TotalSeconds:F1} s ago" : "never";
            return $"Header: {HeaderPath}\n  Data area: {_dataLayout.Size} bytes, last update: {age}";
        }
    }

    public string? Problem => HasData
        ? null
        : $"No data from the {_sdk.DisplayName} yet. Add EnableDataBroadcast=1 (and EnableCDUBroadcast.0=1) under [SDK] in {_sdk.OptionsFile} and reload the aircraft.";

    public bool? GetBool(string field, int index = 0)
    {
        lock (_gate) return _data?.GetBool(field, index);
    }

    public double? GetNumber(string field, int index = 0)
    {
        lock (_gate) return _data?.GetNumber(field, index);
    }

    public string? GetString(string field)
    {
        lock (_gate) return _data?.GetString(field);
    }

    public PmdgCduScreen? GetCdu(int index)
    {
        lock (_gate) return index >= 0 && index < _cdus.Length ? _cdus[index] : null;
    }

    public bool SendEvent(string eventName, uint parameter = 0)
    {
        var id = _header.GetNumber(eventName);
        if (id is null)
        {
            return false;
        }

        // PMDG events are custom events addressed by number, e.g. "#83637".
        _bridge.TriggerEvent($"#{id}", parameter);
        return true;
    }

    public void OnOpen(SimConnect simConnect)
    {
        Subscribe(simConnect, _sdk.DataNameDefine, _sdk.DataIdDefine, _sdk.DataDefinitionDefine, _dataLayout.Size, DataRequestId);
        for (var i = 0; i < _sdk.CduDefines.Count; i++)
        {
            var (name, id, definition) = _sdk.CduDefines[i];
            Subscribe(simConnect, name, id, definition, _cduLayout.Size, CduRequestBase + (uint)i);
        }
    }

    public void OnClientData(SIMCONNECT_RECV_CLIENT_DATA data)
    {
        if (data.dwData.Length == 0 || data.dwData[0] is not RawChunks raw)
        {
            return;
        }

        if (data.dwRequestID == DataRequestId)
        {
            var block = new PmdgDataBlock(_dataLayout, ClientDataChunks.Assemble(raw.Bytes, _dataLayout.Size));
            lock (_gate)
            {
                _data = block;
                LastDataUtc = DateTime.UtcNow;
            }
        }
        else if (data.dwRequestID >= CduRequestBase && data.dwRequestID < CduRequestBase + _cdus.Length)
        {
            var screen = PmdgCduScreen.Decode(_header, _sdk.CduStruct, ClientDataChunks.Assemble(raw.Bytes, _cduLayout.Size));
            lock (_gate) _cdus[data.dwRequestID - CduRequestBase] = screen;
        }
    }

    public void OnClose()
    {
        lock (_gate)
        {
            _data = null;
            Array.Clear(_cdus);
        }
    }

    private void Subscribe(SimConnect sc, string nameDefine, string idDefine, string definitionDefine, int size, uint requestId)
    {
        var name = _header.GetString(nameDefine) ?? throw new InvalidDataException($"{nameDefine} missing in {_header.SourcePath}");
        var clientDataId = (ClientDataId)(uint)(_header.GetNumber(idDefine) ?? throw new InvalidDataException($"{idDefine} missing"));
        var definitionId = (DefinitionId)(uint)(_header.GetNumber(definitionDefine) ?? throw new InvalidDataException($"{definitionDefine} missing"));

        sc.MapClientDataNameToID(name, clientDataId);
        foreach (var offset in ClientDataChunks.Offsets(size))
        {
            sc.AddToClientDataDefinition(definitionId, (uint)offset, ClientDataChunks.ChunkSize, 0, SimConnect.SIMCONNECT_UNUSED);
        }

        sc.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, RawChunks>(definitionId);
        // Every 10th visual frame, whether or not it changed: with ON_SET + CHANGED a cold and dark aircraft
        // that never changes its data sent nothing at all after connecting.
        sc.RequestClientData(
            clientDataId, (RequestId)requestId, definitionId,
            SIMCONNECT_CLIENT_DATA_PERIOD.VISUAL_FRAME, SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT, 0, 10, 0);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RawChunks
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ClientDataChunks.BufferSize)]
        public byte[] Bytes;
    }
}
