using Microsoft.FlightSimulator.SimConnect;

namespace Rampside.MsfsBridge;

/// <summary>Adds its own SimConnect requests to a <see cref="SimConnectBridge"/>. All calls come from the bridge's message thread.</summary>
public interface ISimConnectExtension
{
    void OnOpen(SimConnect simConnect);
    void OnClientData(SIMCONNECT_RECV_CLIENT_DATA data);
    void OnClose();
}
