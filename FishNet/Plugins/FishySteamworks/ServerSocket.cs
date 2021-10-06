#if !FISHYSTEAMWORKS
using FishNet.Transporting;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FishySteamworks.Server
{
    public class ServerSocket : CommonSocket
    {
        #region Public.
        /// <summary>
        /// Gets the current ConnectionState of a remote client on the server.
        /// </summary>
        /// <param name="connectionId">ConnectionId to get ConnectionState for.</param>
        internal RemoteConnectionStates GetConnectionState(int connectionId)
        {
            //Remote clients can only have Started or Stopped states since we cannot know in between.
            if (_steamConnections.Second.ContainsKey(connectionId))
                return RemoteConnectionStates.Started;
            else
                return RemoteConnectionStates.Stopped;
        }
        #endregion

        #region Private.
        /// <summary>
        /// SteamConnections for ConnectionIds.
        /// </summary>
        private BidirectionalDictionary<HSteamNetConnection, int> _steamConnections = new BidirectionalDictionary<HSteamNetConnection, int>();
        /// <summary>
        /// SteamIds for ConnectionIds.
        /// </summary>
        private BidirectionalDictionary<CSteamID, int> _steamIds = new BidirectionalDictionary<CSteamID, int>();
        /// <summary>
        /// Maximum number of remote connections.
        /// </summary>
        private int _maximumClients;
        /// <summary>
        /// Next Id to use for a connection.
        /// </summary>
        private int _nextConnectionId;
        /// <summary>
        /// Socket for the connection.
        /// </summary>
        private HSteamListenSocket _socket = new HSteamListenSocket(0);
        /// <summary>
        /// Called when a remote connection state changes.
        /// </summary>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _onRemoteConnectionStateCallback = null;
        /// <summary>
        /// ConnectionIds which can be reused.
        /// </summary>
        private Queue<int> _cachedConnectionIds = new Queue<int>();
        #endregion

        /// <summary>
        /// Initializes this for use.
        /// </summary>
        /// <param name="t"></param>
        internal override void Initialize(Transport t)
        {
            base.Initialize(t);
            _onRemoteConnectionStateCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnRemoteConnectionState);
        }

        /// <summary>
        /// Resets the socket if invalid.
        /// </summary>
        internal void ResetInvalidSocket()
        {
            /* Force connection state to stopped if listener is invalid.
            * Not sure if steam may change this internally so better
            * safe than sorry and check before trying to connect
            * rather than being stuck in the incorrect state. */
            if (_socket == HSteamListenSocket.Invalid)
                base.SetConnectionState(LocalConnectionStates.Stopped);
        }
        /// <summary>
        /// Starts the server.
        /// </summary>
        internal bool StartConnection(string address, ushort port, int maximumClients, bool peerToPeer)
        {
            base.PeerToPeer = peerToPeer;

            //If address is required then make sure it can be parsed.
            byte[] ip = (!peerToPeer) ? base.GetIPBytes(address) : null;

            base.PeerToPeer = peerToPeer;
            SetMaximumClients(maximumClients);
            _nextConnectionId = 0;
            _cachedConnectionIds.Clear();

            base.SetConnectionState(LocalConnectionStates.Starting);
            SteamNetworkingConfigValue_t[] options = new SteamNetworkingConfigValue_t[] { };

            if (base.PeerToPeer)
            {
#if UNITY_SERVER
                _socket = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
#else
                _socket = SteamNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
#endif
            }
            else
            {
                SteamNetworkingIPAddr addr = new SteamNetworkingIPAddr();
                addr.Clear();
                if (ip != null)
                    addr.SetIPv6(ip, port);
#if UNITY_SERVER
                _socket = SteamGameServerNetworkingSockets.CreateListenSocketIP(ref addr, 0, options);
#else
                _socket = SteamNetworkingSockets.CreateListenSocketIP(ref addr, 0, options);
#endif
            }

            return true;
        }

        /// <summary>
        /// Stops the local socket.
        /// </summary>
        internal bool StopConnection()
        {
            if (base.GetConnectionState() == LocalConnectionStates.Stopped)
                return false;

            base.SetConnectionState(LocalConnectionStates.Stopping);
#if UNITY_SERVER
            SteamGameServerNetworkingSockets.CloseListenSocket(_socket);
#else
            SteamNetworkingSockets.CloseListenSocket(_socket);
#endif
            if (_onRemoteConnectionStateCallback != null)
            {
                _onRemoteConnectionStateCallback.Dispose();
                _onRemoteConnectionStateCallback = null;
            }
            _socket = HSteamListenSocket.Invalid;
            base.SetConnectionState(LocalConnectionStates.Stopped);

            return true;
        }

        /// <summary>
        /// Stops a remote client from the server, disconnecting the client.
        /// </summary>
        /// <param name="connectionId">ConnectionId of the client to disconnect.</param>
        internal bool StopConnection(int connectionId)
        {
            if (_steamConnections.Second.TryGetValue(connectionId, out HSteamNetConnection steamConn))
            {
                return StopConnection(connectionId, steamConn);
            }
            else
            {
                Debug.LogError($"Steam connection not found for connectionId {connectionId}.");
                return false;
            }
        }
        /// <summary>
        /// Stops a remote client from the server, disconnecting the client.
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="socket"></param>
        private bool StopConnection(int connectionId, HSteamNetConnection socket)
        {
#if UNITY_SERVER
            SteamGameServerNetworkingSockets.CloseConnection(socket, 0, string.Empty, false);
#else
            SteamNetworkingSockets.CloseConnection(socket, 0, string.Empty, false);
#endif
            _steamConnections.Remove(connectionId);
            _steamIds.Remove(connectionId);
            Debug.Log($"Client with ConnectionID {connectionId} disconnected.");
            base.Transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionStates.Stopped, connectionId));
            _cachedConnectionIds.Enqueue(connectionId);

            return true;
        }

        /// <summary>
        /// Called when a remote connection state changes.
        /// </summary>
        private void OnRemoteConnectionState(SteamNetConnectionStatusChangedCallback_t args)
        {
            ulong clientSteamID = args.m_info.m_identityRemote.GetSteamID64();
            if (args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
            {
                if (_steamConnections.Count >= _maximumClients)
                {
                    Debug.Log($"Incoming connection {clientSteamID} would exceed max connection count. Rejecting.");
#if UNITY_SERVER
                    SteamGameServerNetworkingSockets.CloseConnection(args.m_hConn, 0, "Max Connection Count", false);
#else
                    SteamNetworkingSockets.CloseConnection(args.m_hConn, 0, "Max Connection Count", false);
#endif
                    return;
                }

#if UNITY_SERVER
                EResult res = SteamGameServerNetworkingSockets.AcceptConnection(args.m_hConn);
#else
                EResult res = SteamNetworkingSockets.AcceptConnection(args.m_hConn);
#endif
                if (res == EResult.k_EResultOK)
                {
                    Debug.Log($"Accepting connection {clientSteamID}");
                }
                else
                {
                    Debug.Log($"Connection {clientSteamID} could not be accepted: {res.ToString()}");
                }
            }
            else if (args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                int connectionId = (_cachedConnectionIds.Count > 0) ? _cachedConnectionIds.Dequeue() : _nextConnectionId++;
                _steamConnections.Add(args.m_hConn, connectionId);
                _steamIds.Add(args.m_info.m_identityRemote.GetSteamID(), connectionId);
                Debug.Log($"Client with SteamID {clientSteamID} connected. Assigning connection id {connectionId}");
                base.Transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionStates.Started, connectionId));
            }
            else if (args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || args.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                if (_steamConnections.TryGetValue(args.m_hConn, out int connId))
                {
                    StopConnection(connId, args.m_hConn);
                }
            }
            else
            {
                Debug.Log($"Connection {clientSteamID} state changed: {args.m_info.m_eState.ToString()}");
            }
        }


        /// <summary>
        /// Allows for Outgoing queue to be iterated.
        /// </summary>
        internal void IterateOutgoing()
        {
            if (base.GetConnectionState() != LocalConnectionStates.Started)
                return;

            foreach (HSteamNetConnection conn in _steamConnections.FirstTypes)
            {
#if UNITY_SERVER
                SteamGameServerNetworkingSockets.FlushMessagesOnConnection(conn);
#else
                SteamNetworkingSockets.FlushMessagesOnConnection(conn);
#endif
            }
        }

        /// <summary>
        /// Iterates the Incoming queue.
        /// </summary>
        /// <param name="transport"></param>
        internal void IterateIncoming()
        {
            //Stopped or trying to stop.
            if (base.GetConnectionState() == LocalConnectionStates.Stopped || base.GetConnectionState() == LocalConnectionStates.Stopping)
                return;

            foreach (KeyValuePair<HSteamNetConnection, int> item in _steamConnections.First)
            {
                HSteamNetConnection steamNetConn = item.Key;
                int connectionId = item.Value;

                int messageCount;
#if UNITY_SERVER
                messageCount = SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(steamNetConn, base.MessagePointers, MAX_MESSAGES);
#else
                messageCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(steamNetConn, base.MessagePointers, MAX_MESSAGES);
#endif
                if (messageCount > 0)
                {
                    for (int i = 0; i < messageCount; i++)
                    {
                        base.GetMessage(base.MessagePointers[i], InboundBuffer, out ArraySegment<byte> segment, out byte channel);
                        base.Transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(segment, (Channel)channel, connectionId));
                    }
                }
            }
        }

        /// <summary>
        /// Sends data to a client.
        /// </summary>
        /// <param name="channelId"></param>
        /// <param name="segment"></param>
        /// <param name="connectionId"></param>
        internal void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            if (base.GetConnectionState() != LocalConnectionStates.Started)
                return;

            if (_steamConnections.TryGetValue(connectionId, out HSteamNetConnection steamConn))
            {
                EResult res = base.Send(steamConn, segment, channelId);

                if (res == EResult.k_EResultNoConnection || res == EResult.k_EResultInvalidParam)
                {
                    Debug.Log($"Connection to {connectionId} was lost.");
                    StopConnection(connectionId, steamConn);
                }
                else if (res != EResult.k_EResultOK)
                {
                    Debug.LogError($"Could not send: {res.ToString()}");
                }
            }
            else
            {
                Debug.LogError("Trying to send on unknown connection: " + connectionId);
            }
        }

        /// <summary>
        /// Gets the address of a remote connection Id.
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        internal string GetConnectionAddress(int connectionId)
        {
            if (_steamIds.TryGetValue(connectionId, out CSteamID steamId))
            {
                return steamId.ToString();
            }
            else
            {
                Debug.LogError("Trying to get info on unknown connection: " + connectionId);
                //OnReceivedError.Invoke(connectionId, new Exception("ERROR Unknown Connection"));
                return string.Empty;
            }
        }


        /// <summary>
        /// Sets maximum number of clients allowed to connect to the server. If applied at runtime and clients exceed this value existing clients will stay connected but new clients may not connect.
        /// </summary>
        /// <param name="value"></param>
        internal void SetMaximumClients(int value)
        {
            _maximumClients = value;
        }
        internal int GetMaximumClients()
        {
            return _maximumClients;
        }
    }
}
#endif // !DISABLESTEAMWORKS