#if !FISHYSTEAMWORKS
using FishNet.Managing;
using FishNet.Managing.Logging;
using FishNet.Transporting;
using Steamworks;
using System;
using System.Threading;
using UnityEngine;

namespace FishySteamworks.Client
{
    public class ClientSocket : CommonSocket
    {
        #region Private.
        /// <summary>
        /// Called when local connection state changes.
        /// </summary>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _onLocalConnectionStateCallback = null;
        /// <summary>
        /// SteamId for host.
        /// </summary>
        private CSteamID _hostSteamID = CSteamID.Nil;
        /// <summary>
        /// Socket to use.
        /// </summary>
        private HSteamNetConnection _socket;
        /// <summary>
        /// Thread used to check for timeout.
        /// </summary>
        private Thread _timeoutThread = null;
        /// <summary>
        /// When connect should timeout in unscaled time.
        /// </summary>
        private float _connectTimeout = -1f;
        #endregion

        #region Const.
        /// <summary>
        /// Maximum time to wait before a timeout occurs when trying ot connect.
        /// </summary>
        private const float CONNECT_TIMEOUT_DURATION = 8000;
        #endregion

        /// <summary>
        /// Initializes this for use.
        /// </summary>
        /// <param name="t"></param>
        internal override void Initialize(Transport t)
        {
            base.Initialize(t);
            _onLocalConnectionStateCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnLocalConnectionState);
        }

        /// <summary>
        /// Checks of a connect attempt should time out.
        /// </summary>
        private void CheckTimeout()
        {
            do
            {
                //Timeout occurred.
                if (Time.unscaledTime > _connectTimeout)
                    StopConnection();

                Thread.Sleep(50);
            } while (base.GetConnectionState() == LocalConnectionStates.Starting);

            //If here then the thread no longer needs to run. Can abort itself.
            _timeoutThread.Abort();
        }

        /// <summary>
        /// Starts the client connection.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="channelsCount"></param>
        /// <param name="pollTime"></param>
        internal bool StartConnection(string address, ushort port, bool peerToPeer)
        {
            //If address is required then make sure it can be parsed.
            byte[] ip = (!peerToPeer) ? base.GetIPBytes(address) : null;
            if (!peerToPeer && ip == null)
                return false;

            SetConnectionState(LocalConnectionStates.Starting);

            _connectTimeout = Time.unscaledTime + CONNECT_TIMEOUT_DURATION;
            _timeoutThread = new Thread(CheckTimeout);
            _timeoutThread.Start();
            _hostSteamID = new CSteamID(UInt64.Parse(address));

            SteamNetworkingIdentity smi = new SteamNetworkingIdentity();
            smi.SetSteamID(_hostSteamID);
            SteamNetworkingConfigValue_t[] options = new SteamNetworkingConfigValue_t[] { };
            if (base.PeerToPeer)
            {
                _socket = SteamNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
            }
            else
            {
                SteamNetworkingIPAddr addr = new SteamNetworkingIPAddr();
                addr.Clear();
                addr.SetIPv6(ip, port);
                _socket = SteamNetworkingSockets.ConnectByIPAddress(ref addr, 0, options);
            }

            return true;
        }


        /// <summary>
        /// Called when local connection state changes.
        /// </summary>
        private void OnLocalConnectionState(SteamNetConnectionStatusChangedCallback_t args)
        {
            if (args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                SetConnectionState(LocalConnectionStates.Started);
            }
            else if (args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                if (base.Transport.NetworkManager.CanLog(LoggingType.Common))
                    Debug.Log($"Connection was closed by peer, {args.m_info.m_szEndDebug}");
                StopConnection();
            }
            else
            {
                if (base.Transport.NetworkManager.CanLog(LoggingType.Common))
                    Debug.Log($"Connection state changed: {args.m_info.m_eState.ToString()} - {args.m_info.m_szEndDebug}");
            }
        }

        /// <summary>
        /// Stops the local socket.
        /// </summary>
        internal bool StopConnection()
        {
            if (base.GetConnectionState() == LocalConnectionStates.Stopped || base.GetConnectionState() == LocalConnectionStates.Stopping)
                return false;

            SetConnectionState(LocalConnectionStates.Stopping);
            //Manually abort thread to close it down quicker.
            if (_timeoutThread.IsAlive)
                _timeoutThread.Abort();

            //Reset callback.
            if (_onLocalConnectionStateCallback != null)
            {
                _onLocalConnectionStateCallback.Dispose();
                _onLocalConnectionStateCallback = null;
            }

            SteamNetworkingSockets.CloseConnection(_socket, 0, string.Empty, false);
            _socket.m_HSteamNetConnection = 0;
            SetConnectionState(LocalConnectionStates.Stopped);

            return true;
        }

        /// <summary>
        /// Iterations data received.
        /// </summary>
        internal void IterateIncoming()
        {
            if (base.GetConnectionState() != LocalConnectionStates.Started)
                return;

            int messageCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(_socket, base.MessagePointers, MAX_MESSAGES);
            if (messageCount > 0)
            {
                for (int i = 0; i < messageCount; i++)
                {
                    base.GetMessage(base.MessagePointers[i], InboundBuffer, out ArraySegment<byte> segment, out byte channel);
                    base.Transport.HandleClientReceivedDataArgs(new ClientReceivedDataArgs(segment, (Channel)channel));
                }
            }
        }

        /// <summary>
        /// Queues data to be sent to server.
        /// </summary>
        /// <param name="channelId"></param>
        /// <param name="segment"></param>
        internal void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (base.GetConnectionState() != LocalConnectionStates.Started)
                return;

            EResult res = base.Send(_socket, segment, channelId);
            if (res == EResult.k_EResultNoConnection || res == EResult.k_EResultInvalidParam)
            {
                if (base.Transport.NetworkManager.CanLog(LoggingType.Common))
                    Debug.Log($"Connection to server was lost.");
                StopConnection();
            }
            else if (res != EResult.k_EResultOK)
            {
                if (base.Transport.NetworkManager.CanLog(LoggingType.Error))
                    Debug.LogError($"Could not send: {res.ToString()}");
            }
        }


        /// <summary>
        /// Sends queued data to server.
        /// </summary>
        internal void IterateOutgoing()
        {
            if (base.GetConnectionState() != LocalConnectionStates.Started)
                return;

            SteamNetworkingSockets.FlushMessagesOnConnection(_socket);
        }

    }
}
#endif // !DISABLESTEAMWORKS