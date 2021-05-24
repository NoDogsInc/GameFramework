using Cube.Networking;
using Cube.Replication;
using Cube.Transport;
using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Assertions;
using UnityEngine.Events;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement; // SceneManager
using BitStream = Cube.Transport.BitStream;

namespace GameFramework {
    [Serializable]
    public class ConnectionEvent : UnityEvent<Connection> {
    }

    public struct ServerGameContext {
        public World World;
        public ushort Port;
        public ServerReplicaManagerSettings ReplicaManagerSettings;
        public SimulatedLagSettings LagSettings;
    }

    public class ServerGame {
        public CubeServer server {
            get;
            internal set;
        }
        public IGameMode gameMode {
            get;
            internal set;
        }
        public World world {
            get;
            internal set;
        }

        public ConnectionEvent NewIncomingConnection = new ConnectionEvent();
        public ConnectionEvent DisconnectionNotification = new ConnectionEvent();
        public UnityEvent AllClientsLoadedScene = new UnityEvent();

        AsyncOperationHandle<SceneInstance> sceneHandle;
        string loadSceneName;
        byte loadSceneGeneration;
        byte numLoadScenePlayerAcks;
        bool triggeredAllClientsLoadedScene;

        public ServerGame(ServerGameContext ctx) {
            Assert.IsNotNull(ctx.World);
            world = ctx.World;

            //var networkInterface = new LidgrenServerNetworkInterface(ctx.Settings.Port, ctx.LagSettings);
            var networkInterface = new LiteNetServerNetworkInterface(ctx.Port);
            server = new CubeServer(ctx.World, networkInterface, ctx.ReplicaManagerSettings);

            server.NetworkInterface.ApproveConnection += OnApproveConnection;
            server.NetworkInterface.NewConnectionEstablished += OnNewIncomingConnection;
            server.NetworkInterface.DisconnectNotification += OnDisconnectNotification;
            server.Reactor.AddMessageHandler((byte)MessageId.LoadSceneDone, OnLoadSceneDone);
        }

        public virtual IGameMode CreateGameModeForScene(string sceneName) {
            return new GameMode(this);
        }

        protected virtual ApprovalResult OnApproveConnection(BitStream bs) {
            return new ApprovalResult() { Approved = true };
        }

        /// <summary>
        /// Reset replication, instruct all clients to load the new scene, actually
        /// load the new scene on the server and finally create a new GameMode instance.
        /// </summary>
        /// <param name="sceneName"></param>
        public void LoadScene(string sceneName) {
            // Cleanup
            if (gameMode != null) {
                gameMode.StartToLeaveMap();
            }

            ++loadSceneGeneration;
            numLoadScenePlayerAcks = 0;
            loadSceneName = sceneName;
            triggeredAllClientsLoadedScene = false;

            server.ReplicaManager.Reset();
            if (sceneHandle.IsValid()) {
                Addressables.UnloadSceneAsync(sceneHandle);
            }

            // Instruct clients
            var bs = new BitStream();
            bs.Write((byte)MessageId.LoadScene);
            bs.Write(sceneName);
            bs.Write(loadSceneGeneration);

            server.NetworkInterface.BroadcastBitStream(bs, PacketPriority.High, PacketReliability.ReliableSequenced);

            // Disable ReplicaViews during level load
            foreach (var connection in server.connections) {
                var replicaView = server.ReplicaManager.GetReplicaView(connection);
                if (replicaView == null)
                    continue;

                replicaView.IsLoadingLevel = true;
            }

            // Load new map
#if !UNITY_EDITOR || !CLIENT
            Debug.Log($"[Server] Loading level {sceneName}");
            sceneHandle = Addressables.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
#endif

            gameMode = CreateGameModeForScene(sceneName);
            Assert.IsNotNull(gameMode);

            Debug.Log($"[Server] New GameMode <i>{gameMode}</i>");
        }

        void OnNewIncomingConnection(Connection connection) {
            Debug.Log($"[Server] <b>New connection</b> <i>{connection}</i>");

            // Send load scene packet if we loaded one previously
            if (loadSceneName != null) {
                var bs2 = new BitStream();
                bs2.Write((byte)MessageId.LoadScene);
                bs2.Write(loadSceneName);
                bs2.Write(loadSceneGeneration);

                server.NetworkInterface.SendBitStream(bs2, PacketPriority.High, PacketReliability.ReliableSequenced, connection);
            }

            var newPC = CreatePlayerController(connection);
            world.playerControllers.Add(newPC);

            var replicaView = CreateReplicaView(connection);
            server.ReplicaManager.AddReplicaView(replicaView);

            gameMode.HandleNewPlayer(newPC);

            NewIncomingConnection.Invoke(connection);
        }

        protected virtual PlayerController CreatePlayerController(Connection connection) {
            return new PlayerController(connection);
        }

        protected virtual void OnDisconnectNotification(Connection connection) {
            Debug.Log("[Server] Lost connection: " + connection);

            DisconnectionNotification.Invoke(connection);

            server.ReplicaManager.RemoveReplicaView(connection);

            OnNumReadyClientsChanged();
        }

        public virtual void Update() {
            server.Update();
            if (gameMode != null) {
                gameMode.Update();
            }
        }

        public virtual void Shutdown() {
            server.Shutdown();
        }

        ReplicaView CreateReplicaView(Connection connection) {
            var view = new GameObject("ReplicaView " + connection);
            view.transform.parent = server.World.transform;

            var rw = view.AddComponent<ReplicaView>();
            rw.Connection = connection;

            return rw;
        }

        void OnLoadSceneDone(Connection connection, BitStream bs) {
            var generation = bs.ReadByte();
            if (generation != loadSceneGeneration)
                return;

            Debug.Log("[Server] On load scene done: <i>" + connection + "</i> (generation=" + generation + ")");

            ++numLoadScenePlayerAcks;

            OnNumReadyClientsChanged();

            //
            var replicaView = server.ReplicaManager.GetReplicaView(connection);
            if (replicaView != null) {
                replicaView.IsLoadingLevel = false;
                server.ReplicaManager.ForceReplicaViewRefresh(replicaView);
            }
        }

        void OnNumReadyClientsChanged() {
            if (!triggeredAllClientsLoadedScene && numLoadScenePlayerAcks >= server.connections.Count) {
                triggeredAllClientsLoadedScene = true;
                AllClientsLoadedScene.Invoke();
            }
        }
    }
}