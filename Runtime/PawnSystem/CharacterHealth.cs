﻿using System;
using Cube.Replication;
using UnityEngine;
using UnityEngine.Assertions;
using BitStream = Cube.Transport.BitStream;

namespace GameFramework {
    public class HurtEvent : IEvent { }

    public class DeathEvent : IEvent { }

    public class CharacterHealth : ReplicaBehaviour, IDamageable {
        public delegate void DamageEvent(DamageInfo? reason);

        public event DamageEvent OnDeath;
        public event DamageEvent OnDamage;

        /// <summary>
        /// [0,1]
        /// </summary>
        public float PercentHealth => Health / (float)MaxHealth;

        public bool IsDead => Health <= 0;

        public int Health;
        public byte MaxHealth = 100;

        DamageInfo? deathReason;

#if UNITY_EDITOR
        [ContextMenu("Kill")]
        void KillInEditor() {
            Kill(new DamageInfo(255, Vector3.zero, Vector3.zero));
        }
#endif

        public void Kill(DamageInfo reason) {
            Assert.IsTrue(isServer);

            deathReason = reason;
            try {
                OnDeath?.Invoke(reason);
            } finally {
                Replica.Destroy();
            }
        }

        public void Heal(int amount) {
            if (IsDead)
                return;

            Health = Math.Max(Health + amount, MaxHealth);
        }

        public void ApplyDamage(DamageInfo info) {
            if (IsDead)
                return;

            Health = Math.Max(Health - info.Amount, 0);
            if (Health == 0) {
                if (isServer) {
                    Kill(info);
                }
            } else {
                OnDamage?.Invoke(info);
                if (isServer) {
                    RpcOwnerHurt();
                }
            }
        }

        public override void Serialize(BitStream bs, SerializeContext ctx) {
            Assert.IsTrue(Health >= 0 && Health <= 255);
            bs.Write((byte)Health);
        }

        public override void Deserialize(BitStream bs) {
            var newHealth = bs.ReadByte();
            if (newHealth != Health) {
                if (newHealth < Health) {
                    OnDamage?.Invoke(null);
                    if (isOwner) {
                        EventHub<HurtEvent>.Emit(new HurtEvent());
                    }
                }
                Health = newHealth;
            }
        }

        public override void SerializeDestruction(BitStream bs, SerializeContext ctx) {
            bs.Write(deathReason.HasValue);
            if (deathReason.HasValue) {
                bs.Write(deathReason.Value);
            }
        }

        public override void DeserializeDestruction(BitStream bs) {
            DamageInfo? reason = null;
            var hasDeathReason = bs.ReadBool();
            if (hasDeathReason) {
                var newReason = new DamageInfo();
                newReason.Deserialize(bs);
                reason = newReason;
            }
            OnDeath?.Invoke(reason);
            if (isOwner) {
                EventHub<DeathEvent>.Emit(new DeathEvent());
            }
        }

        void Awake() {
            Health = MaxHealth;
        }

        [ReplicaRpc(RpcTarget.Owner)]
        void RpcOwnerHurt() {
            if (isServer)
                return;

            OnDamage?.Invoke(null);
        }
    }
}