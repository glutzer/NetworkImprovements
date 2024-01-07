using HarmonyLib;
using ProperVersion;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.Server;

public class Patches
{
    // Postfix of the server receiving the client's position. Will call something in player physics.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "HandlePlayerPosition")]
    public static class PacketPostfix1
    {
        [HarmonyPostfix]
        public static void Postfix(Packet_Client packet, ConnectedClient client)
        {
            //client.Player.Entity.GetBehavior<EntityPlayerPhysics>().OnReceivedClientPos();
        }
    }

    // Set delta here in a patch.
    public static float dt = 0;
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "UpdateEvery200ms")]
    public static class PacketPrefix2
    {
        [HarmonyPostfix]
        public static void Prefix(float dt)
        {
            Patches.dt = dt;
        }
    }

    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "SendEntityAttributeUpdates")]
    public static class PacketPrefix3
    {
        [HarmonyPostfix]
        public static void Prefix(ServerSystemEntitySimulation __instance)
        {
            // Set watched attribute on player dt float.
            // Now in receivedServerPos I can get the delta of the new value from the player.
            foreach (ConnectedClient client in __instance.GetField<ServerMain>("server").Clients.Values)
            {
                client?.Player?.Entity?.WatchedAttributes.SetFloat("lastDelta", dt);
            }
        }
    }

    // Nametags and some positions break if the position is set here. I tried calling renderframe after received server pos in interpolation but it didn't help.
    [HarmonyPatch(typeof(Entity), "OnReceivedServerPos")]
    public static class MovedPrefix
    {
        [HarmonyPrefix]
        public static bool Prefix(Entity __instance, bool isTeleport)
        {
            EnumHandling handled = EnumHandling.PassThrough;
            foreach (EntityBehavior behavior in __instance.SidedProperties.Behaviors)
            {
                behavior.OnReceivedServerPos(isTeleport, ref handled);
                if (handled == EnumHandling.PreventSubsequent)
                {
                    break;
                }
            }

            if (handled == EnumHandling.PassThrough && !__instance.HasBehavior<EntityInterpolation>())
            {
                __instance.Pos.SetFrom(__instance.ServerPos);
            }

            return false;
        }
    }

    /// <summary>
    /// Patch this one AI Task to also try to get step height from the new physics.
    /// </summary>
    [HarmonyPatch(typeof(AiTaskBaseTargetable), "StartExecute")]
    public static class StartExecutePostfix
    {
        [HarmonyPostfix]
        public static void Postfix(AiTaskBaseTargetable __instance)
        {
            //Use this physics step height instead if it exists
            EntityControlledPhysics physics = __instance.entity.GetBehavior<EntityControlledPhysics>();
            if (physics != null)
            {
                __instance.SetField("stepHeight", physics.stepHeight);
            }
        }
    }

    /// <summary>
    /// Override initialize to use new behaviors since I found no easy way to do this.
    /// </summary>
    [HarmonyPatch(typeof(EntityProjectile), "Initialize")]
    public static class InitializePrefix
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityProjectile __instance, EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            EntityProjectile entity = __instance;

            __instance.World = api.World;
            entity.Api = api;

            if (entity.Api.Side == EnumAppSide.Server)
            {
                if (entity.FiredBy != null)
                {
                    entity.WatchedAttributes.SetLong("firedBy", entity.FiredBy.EntityId);
                }
            }

            if (entity.Api.Side == EnumAppSide.Client)
            {
                entity.FiredBy = __instance.Api.World.GetEntityById(entity.WatchedAttributes.GetLong("firedBy"));
            }

            entity.SetProperty("Properties", properties);
            entity.Class = properties.Class;
            entity.InChunkIndex3d = InChunkIndex3d;
            entity.Alive = entity.WatchedAttributes.GetInt("entityDead") == 0;
            entity.WatchedAttributes.SetFloat("onHurt", 0f);
            int onHurtCounter = entity.WatchedAttributes.GetInt("onHurtCounter");
            entity.WatchedAttributes.RegisterModifiedListener("onHurt", delegate
            {
                float onHurtAttribute = entity.WatchedAttributes.GetFloat("onHurt");
                if (onHurtAttribute != 0f)
                {
                    int onHurtCounterAttribute = entity.WatchedAttributes.GetInt("onHurtCounter");
                    if (onHurtCounterAttribute != onHurtCounter)
                    {
                        onHurtCounter = onHurtCounterAttribute;
                        if (entity.Attributes.GetInt("dmgkb") == 0)
                        {
                            entity.Attributes.SetInt("dmgkb", 1);
                        }

                        if ((double)onHurtAttribute > 0.05)
                        {
                            entity.SetActivityRunning("invulnerable", 500);
                            if (entity.World.Side == EnumAppSide.Client)
                            {
                                entity.OnHurt(null, entity.WatchedAttributes.GetFloat("onHurt"));
                            }
                        }
                    }
                }
            });
            entity.WatchedAttributes.RegisterModifiedListener("onFire", () => entity.CallMethod("updateOnFire"));
            entity.WatchedAttributes.RegisterModifiedListener("entityDead", () => entity.CallMethod("updateColSelBoxes"));
            if (entity.World.Side == EnumAppSide.Client && entity.Properties.Client.SizeGrowthFactor != 0f)
            {
                entity.WatchedAttributes.RegisterModifiedListener("grow", delegate
                {
                    float sizeGrowthFactor = entity.Properties.Client.SizeGrowthFactor;
                    if (sizeGrowthFactor != 0f)
                    {
                        EntityClientProperties client = entity.World.GetEntityType(entity.Code).Client;
                        entity.Properties.Client.Size = client.Size + entity.WatchedAttributes.GetTreeAttribute("grow").GetFloat("age") * sizeGrowthFactor;
                    }
                });
            }

            if (entity.Properties.CollisionBoxSize != null || properties.SelectionBoxSize != null)
            {
                entity.CallMethod("updateColSelBoxes");
            }

            entity.CallMethod("DoInitialActiveCheck", api);
            if (api.Side == EnumAppSide.Server && properties.Client?.FirstTexture?.Alternates != null && !entity.WatchedAttributes.HasAttribute("textureIndex"))
            {
                entity.WatchedAttributes.SetInt("textureIndex", entity.World.Rand.Next(properties.Client.FirstTexture.Alternates.Length + 1));
            }

            entity.Properties.Initialize(entity, api);
            entity.Properties.Client.DetermineLoadedShape(entity.EntityId);
            if (api.Side == EnumAppSide.Server)
            {
                entity.AnimManager = AnimationCache.InitManager(api, entity.AnimManager, entity, properties.Client.LoadedShapeForEntity, null, "head");
                entity.AnimManager.OnServerTick(0f);
            }
            else
            {
                entity.AnimManager.Init(api, entity);
            }

            entity.LocalEyePos.Y = entity.Properties.EyeHeight;
            entity.CallMethod("TriggerOnInitialized");

            // Projectile initializer here.
            entity.SetField("msLaunch", entity.World.ElapsedMilliseconds);
            entity.SetField("collisionTestBox", entity.SelectionBox.Clone().OmniGrowBy(0.05f));
            entity.GetBehavior<EntityPassivePhysics>().OnPhysicsTickCallback = (accum) => entity.CallMethod("onPhysicsTickCallback", accum);
            entity.SetField("ep", api.ModLoader.GetModSystem<EntityPartitioning>());
            entity.GetBehavior<EntityPassivePhysics>().collisionYExtra = 0f;

            return false;
        }
    }

    /// <summary>
    /// Override initialize to use new behaviors since I found no easy way to do this.
    /// </summary>
    [HarmonyPatch(typeof(EntityThrownStone), "Initialize")]
    public static class InitializeStonePrefix
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityProjectile __instance, EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            EntityProjectile entity = __instance;

            __instance.World = api.World;
            entity.Api = api;

            if (entity.Api.Side == EnumAppSide.Server)
            {
                if (entity.FiredBy != null)
                {
                    entity.WatchedAttributes.SetLong("firedBy", entity.FiredBy.EntityId);
                }
            }

            if (entity.Api.Side == EnumAppSide.Client)
            {
                entity.FiredBy = __instance.Api.World.GetEntityById(entity.WatchedAttributes.GetLong("firedBy"));
            }

            entity.SetProperty("Properties", properties);
            entity.Class = properties.Class;
            entity.InChunkIndex3d = InChunkIndex3d;
            entity.Alive = entity.WatchedAttributes.GetInt("entityDead") == 0;
            entity.WatchedAttributes.SetFloat("onHurt", 0f);
            int onHurtCounter = entity.WatchedAttributes.GetInt("onHurtCounter");
            entity.WatchedAttributes.RegisterModifiedListener("onHurt", delegate
            {
                float onHurtAttribute = entity.WatchedAttributes.GetFloat("onHurt");
                if (onHurtAttribute != 0f)
                {
                    int onHurtCounterAttribute = entity.WatchedAttributes.GetInt("onHurtCounter");
                    if (onHurtCounterAttribute != onHurtCounter)
                    {
                        onHurtCounter = onHurtCounterAttribute;
                        if (entity.Attributes.GetInt("dmgkb") == 0)
                        {
                            entity.Attributes.SetInt("dmgkb", 1);
                        }

                        if ((double)onHurtAttribute > 0.05)
                        {
                            entity.SetActivityRunning("invulnerable", 500);
                            if (entity.World.Side == EnumAppSide.Client)
                            {
                                entity.OnHurt(null, entity.WatchedAttributes.GetFloat("onHurt"));
                            }
                        }
                    }
                }
            });
            entity.WatchedAttributes.RegisterModifiedListener("onFire", () => entity.CallMethod("updateOnFire"));
            entity.WatchedAttributes.RegisterModifiedListener("entityDead", () => entity.CallMethod("updateColSelBoxes"));
            if (entity.World.Side == EnumAppSide.Client && entity.Properties.Client.SizeGrowthFactor != 0f)
            {
                entity.WatchedAttributes.RegisterModifiedListener("grow", delegate
                {
                    float sizeGrowthFactor = entity.Properties.Client.SizeGrowthFactor;
                    if (sizeGrowthFactor != 0f)
                    {
                        EntityClientProperties client = entity.World.GetEntityType(entity.Code).Client;
                        entity.Properties.Client.Size = client.Size + entity.WatchedAttributes.GetTreeAttribute("grow").GetFloat("age") * sizeGrowthFactor;
                    }
                });
            }

            if (entity.Properties.CollisionBoxSize != null || properties.SelectionBoxSize != null)
            {
                entity.CallMethod("updateColSelBoxes");
            }

            entity.CallMethod("DoInitialActiveCheck", api);
            if (api.Side == EnumAppSide.Server && properties.Client?.FirstTexture?.Alternates != null && !entity.WatchedAttributes.HasAttribute("textureIndex"))
            {
                entity.WatchedAttributes.SetInt("textureIndex", entity.World.Rand.Next(properties.Client.FirstTexture.Alternates.Length + 1));
            }

            entity.Properties.Initialize(entity, api);
            entity.Properties.Client.DetermineLoadedShape(entity.EntityId);
            if (api.Side == EnumAppSide.Server)
            {
                entity.AnimManager = AnimationCache.InitManager(api, entity.AnimManager, entity, properties.Client.LoadedShapeForEntity, null, "head");
                entity.AnimManager.OnServerTick(0f);
            }
            else
            {
                entity.AnimManager.Init(api, entity);
            }

            entity.LocalEyePos.Y = entity.Properties.EyeHeight;
            entity.CallMethod("TriggerOnInitialized");

            // Projectile initializer here.
            entity.SetField("msLaunch", entity.World.ElapsedMilliseconds);
            if (entity.ProjectileStack?.Collectible != null)
            {
                entity.ProjectileStack.ResolveBlockOrItem(entity.World);
            }

            entity.GetBehavior<EntityPassivePhysics>().collisionYExtra = 0f;

            return false;
        }
    }

    [HarmonyPatch(typeof(EntityBlockFalling), "OnGameTick")]
    public static class TickPrefix
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityBlockFalling __instance, float dt, ref float ___soundStartDelay, ref ILoadedSound ___sound, ref int ___lingerTicks, ref float ___nowDustIntensity, ref bool ___fallHandled, ref Vec3d ___fallMotion, ref int ___ticksAlive, ref float ___pushaccum)
        {
            EntityBlockFalling entity = __instance;

            entity.World.FrameProfiler.Enter("entity-tick-unsstablefalling");

            if (___soundStartDelay > 0)
            {
                ___soundStartDelay -= dt;
                if (___soundStartDelay <= 0)
                {
                    ___sound.Start();
                }
            }

            ___sound?.SetPosition((float)entity.Pos.X, (float)entity.Pos.Y, (float)entity.Pos.Z);

            if (___lingerTicks > 0)
            {
                ___lingerTicks--;
                if (___lingerTicks == 0)
                {
                    if (entity.Api.Side == EnumAppSide.Client && ___sound != null)
                    {
                        ___sound.FadeOut(3f, (s) => { s.Dispose(); });
                    }
                    entity.Die();
                }

                return false;
            }

            if (!entity.Collided && !___fallHandled)
            {
                ___nowDustIntensity = 1;
            }
            else
            {
                ___nowDustIntensity = 0;
            }

            entity.World.FrameProfiler.Mark("entity-tick-unsstablefalling-sound(etc)");

            ___ticksAlive++;
            if (___ticksAlive >= 2 || entity.Api.World.Side == EnumAppSide.Client)
            {
                if (!entity.InitialBlockRemoved)
                {
                    entity.InitialBlockRemoved = true;
                    entity.CallMethod("UpdateBlock", true, entity.initialPos);
                }

                foreach (EntityBehavior behavior in entity.SidedProperties.Behaviors)
                {
                    behavior.OnGameTick(dt);
                }
                entity.World.FrameProfiler.Mark("entity-tick-unsstablefalling-physics(etc)");
            }

            ___pushaccum += dt;
            ___fallMotion.X *= 0.99f;
            ___fallMotion.Z *= 0.99f;
            if (___pushaccum > 0.2f)
            {
                ___pushaccum = 0;
                if (!entity.Collided)
                {
                    Entity[] entities;
                    if (entity.Api.Side == EnumAppSide.Server)
                    {
                        entities = entity.World.GetEntitiesAround(entity.SidedPos.XYZ, 1.1f, 1.1f, (e) => !(e is EntityBlockFalling));
                    }
                    else
                    {
                        entities = entity.World.GetEntitiesAround(entity.SidedPos.XYZ, 1.1f, 1.1f, (e) => e is EntityPlayer);
                    }
                    for (int i = 0; i < entities.Length; i++)
                    {
                        entities[i].SidedPos.Motion.Add(___fallMotion.X / 10f, 0, ___fallMotion.Z / 10f);
                    }
                }
            }

            entity.World.FrameProfiler.Mark("entity-tick-unsstablefalling-finalizemotion");
            if (entity.Api.Side == EnumAppSide.Server && !entity.Collided && entity.World.Rand.NextDouble() < 0.01)
            {
                entity.World.BlockAccessor.TriggerNeighbourBlockUpdate(entity.ServerPos.AsBlockPos);
                entity.World.FrameProfiler.Mark("entity-tick-unsstablefalling-neighborstrigger");
            }

            if (entity.CollidedVertically && entity.Pos.Motion.Length() == 0)
            {
                entity.OnFallToGround(0);
                entity.World.FrameProfiler.Mark("entity-tick-unsstablefalling-falltoground");
            }

            entity.World.FrameProfiler.Leave();

            return false;
        }
    }
}