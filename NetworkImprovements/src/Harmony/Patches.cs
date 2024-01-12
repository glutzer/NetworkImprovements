using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using Vintagestory.Server;

public class Patches
{
    // Notify server of teleportation.
    // Client-side only for the client player.
    // Add a version so it's not thrown away.
    [HarmonyPatch(typeof(EntityPlayer), "OnReceivedServerPacket")]
    public static class ReceivedPacketPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(EntityPlayer __instance, int packetid)
        {
            if (packetid != 1) return;

            ICoreClientAPI capi = __instance.Api as ICoreClientAPI;

            if (__instance == capi.World.Player.Entity)
            {
                Packet_Client packet = ClientPackets.PlayerPosition(capi.World.Player.Entity);
                packet.PlayerPosition.PositionVersionNumber += 1;

                (capi.World as ClientMain).SendPacketClient(packet);
            }
        }
    }

    // Postfix of the server receiving the client's position. Will call something in player physics.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "HandlePlayerPosition")]
    public static class HandlePlayerPositionPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(Packet_Client packet, ConnectedClient client)
        {
            client.Player.Entity.GetBehavior<EntityPlayerPhysics>().OnReceivedClientPos(packet.PlayerPosition.PositionVersionNumber);
        }
    }

    // Set delta here in a patch.
    public static float dt = 0;
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "UpdateEvery200ms")]
    public static class UpdateEvery200msPostfix
    {
        [HarmonyPrefix]
        public static void Prefix(float dt)
        {
            Patches.dt = dt;
        }
    }

    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "SendEntityAttributeUpdates")]
    public static class SendEntityAttributeUpdatesPrefix
    {
        [HarmonyPrefix]
        public static void Prefix(ServerSystemEntitySimulation __instance)
        {
            ServerMain server = __instance.GetField<ServerMain>("server");

            // Set watched attribute on player dt float.
            // Now in receivedServerPos I can get the delta of the new value from the player.
            foreach (ConnectedClient client in __instance.GetField<ServerMain>("server").Clients.Values)
            {
                ServerPlayer player = client.Player;

                client?.Player?.Entity?.WatchedAttributes.SetFloat("lastDelta", dt);

                // Time between the server receiving this entity's position and sending it. Subtracted from interval received by client.
                client?.Player?.Entity?.WatchedAttributes.SetFloat("malus", server.ElapsedMilliseconds - player.LastReceivedClientPosition);
            }
        }
    }

    // Nametags and some positions break if the position is set here. I tried calling renderframe after received server pos in interpolation but it didn't help.
    // ServerPos is handled in interpolation.
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

            // Position not set automatically if the entity has interpolation.
            if (handled == EnumHandling.PassThrough && !__instance.HasBehavior<EntityInterpolation>())
            {
                __instance.Pos.SetFrom(__instance.ServerPos);
            }

            return false;
        }
    }

    // Patch this one AI Task to also try to get step height from the new physics.
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

    // Override initialize to use new behaviors since I found no easy way to do this.
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
                        entity.Properties.Client.Size = client.Size + (entity.WatchedAttributes.GetTreeAttribute("grow").GetFloat("age") * sizeGrowthFactor);
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

    // Override initialize to use new behaviors since I found no easy way to do this.
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
                        entity.Properties.Client.Size = client.Size + (entity.WatchedAttributes.GetTreeAttribute("grow").GetFloat("age") * sizeGrowthFactor);
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
}