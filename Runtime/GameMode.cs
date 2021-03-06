﻿using System;
using System.Collections.Generic;
using System.Linq;
using Cube.Transport;
using UnityEngine;

namespace GameFramework {
    public class GameMode : GameModeBase {
        public enum MatchState {
            WaitingToStart,
            InProgress,
            WaitingPostMatch,
            LeavingMap
        }

        public MatchState CurrentMatchState {
            get;
            internal set;
        }

        public bool HasMatchStarted => CurrentMatchState == MatchState.InProgress;
        public bool HasMatchEnded => CurrentMatchState == MatchState.WaitingPostMatch;

        public List<Pawn> Players {
            get;
            internal set;
        }

        Queue<(float, Connection)> respawnQueue = new Queue<(float, Connection)>();

        public GameMode(ServerGame server) : base(server) {
            CurrentMatchState = MatchState.WaitingToStart;
            Players = new List<Pawn>();
            HandleMatchIsWaitingToStart();
        }

        public void StartMatch() {
            if (CurrentMatchState != MatchState.WaitingToStart) {
                Debug.LogWarning("StartMatch called in wrong MatchState " + CurrentMatchState);
                return;
            }

            Debug.Log("[Server] <b>Match starting...</b>");

            CurrentMatchState = MatchState.InProgress;
            HandleMatchHasStarted();
        }

        public void EndMatch() {
            if (CurrentMatchState != MatchState.InProgress) {
                Debug.LogWarning("EndMatch called in wrong MatchState " + CurrentMatchState);
                return;
            }

            CurrentMatchState = MatchState.WaitingPostMatch;
            HandleMatchHasEnded();

            Debug.Log("[Server] <b>Match has ended</b>");
        }

        public override void StartToLeaveMap() {
            if (CurrentMatchState == MatchState.InProgress) {
                EndMatch();
            }

            if (CurrentMatchState != MatchState.WaitingPostMatch) {
                Debug.LogWarning("StartToLeaveMap called in wrong MatchState " + CurrentMatchState);
                return;
            }

            CurrentMatchState = MatchState.LeavingMap;
            HandleLeavingMap();
        }

        public override void Update() {
            switch (CurrentMatchState) {
                case MatchState.WaitingToStart:
                    if (ReadyToStartMatch()) {
                        StartMatch();
                    }
                    break;

                case MatchState.InProgress:
                    foreach (var pc in server.world.playerControllers) {
                        if (pc.Pawn == null && !respawnQueue.Any(pair => pair.Item2 == pc.Connection)) {
                            respawnQueue.Enqueue((Time.time + 5, pc.Connection));
                        }
                    }

                    if (respawnQueue.Count > 0) {
                        var timeControllerPair = respawnQueue.Peek();
                        var respawnPlayer = Time.time >= timeControllerPair.Item1;
                        if (respawnPlayer) {
                            respawnQueue.Dequeue();

                            var pc = server.world.GetPlayerController(timeControllerPair.Item2);
                            if (pc != null && pc.Pawn == null) { // Queued player for respawn but he's already alive
                                SpawnPlayer(pc);
                            }
                        }
                    }

                    if (ReadyToEndMatch()) {
                        EndMatch();
                    }
                    break;
            }
        }

        public override void HandleNewPlayer(PlayerController pc) {
            if (HasMatchStarted) {
                SpawnPlayer(pc);
            }
        }

        protected virtual bool ReadyToStartMatch() {
            return server.server.connections.Count > 0 && !server.IsLoadingScene;
        }

        protected virtual bool ReadyToEndMatch() {
            return server.server.connections.Count == 0 && !server.IsLoadingScene;
        }

        protected virtual void HandleMatchIsWaitingToStart() {
        }

        protected virtual void HandleMatchHasStarted() {
            foreach (var pc in server.world.playerControllers) {
                SpawnPlayer(pc);
            }
        }

        protected virtual void HandleMatchHasEnded() {
        }

        protected virtual void HandleLeavingMap() {
        }

        protected virtual void HandlePlayerSpawned(Pawn player) {
        }

        protected virtual void SpawnPlayer(PlayerController pc) {
            Debug.Log("[Server] <b>Spawning player</b> <i>" + pc.Connection + "</i>");

            var prefabAddress = GetPlayerPrefabAddress(pc);
            var go = server.server.ReplicaManager.InstantiateReplicaAsync(prefabAddress);
            go.Completed += ctx => {
                var character = ctx.Result.GetComponent<Character>();

                var spawnPose = GetPlayerSpawnPosition();
                character.Movement.Teleport(spawnPose.position, spawnPose.rotation);

                Players.Add(character);

                HandlePlayerSpawned(character);

                pc.Possess(character);
            };
        }

        protected virtual object GetPlayerPrefabAddress(PlayerController pc) {
            throw new NotImplementedException();
        }

        protected virtual Pose GetPlayerSpawnPosition() {
            if (PlayerSpawn.all.Count == 0)
                return Pose.identity;

            var spawn = PlayerSpawn.all[UnityEngine.Random.Range(0, PlayerSpawn.all.Count)];

            var spawnPosition = spawn.GetRandomizedPosition();
            return new Pose(spawnPosition, spawn.transform.rotation);
        }
    }
}