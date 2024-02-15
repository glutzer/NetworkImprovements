using HarmonyLib;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

public class Patches
{
    // Falling blocks used previous pos to more smoothly move here. This isn't needed now and breaks because previous pos is set from last received server pos.
    // Do they just look weird now because they're too accurate?
    [HarmonyPatch(typeof(EntityBlockFallingRenderer), "RenderFallingBlockEntity")]
    public static class TestFix
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityBlockFallingRenderer __instance)
        {
            ICoreClientAPI capi = __instance.capi;
            IRenderAPI rapi = capi.Render;

            rapi.GlDisableCullFace();

            rapi.GlToggleBlend(true, EnumBlendMode.Standard);

            double rotaccum = __instance.GetField<double>("rotaccum");

            float div = __instance.entity.Collided ? 4f : 1.5f;

            Vec3d curPos = __instance.GetField<Vec3d>("curPos");

            IStandardShaderProgram prog = rapi.PreparedStandardShader((int)__instance.entity.Pos.X, (int)(__instance.entity.Pos.Y + 0.2), (int)__instance.entity.Pos.Z);
            Vec3d camPos = capi.World.Player.Entity.CameraPos;
            prog.Tex2D = __instance.GetField<int>("atlasTextureId");

            prog.ModelMatrix = __instance.GetField<Matrixf>("ModelMat")
                .Identity()
                .Translate(
                    curPos.X - camPos.X + (GameMath.Sin((capi.InWorldEllapsedMilliseconds / 120f) + 30) / 20f / div),
                    curPos.Y - camPos.Y,
                    curPos.Z - camPos.Z + (GameMath.Cos((capi.InWorldEllapsedMilliseconds / 110f) + 20) / 20f / div)
                )
                .RotateX((float)(Math.Sin(rotaccum * 10) / 10.0 / div))
                .RotateZ((float)(Math.Cos(10 + (rotaccum * 9.0)) / 10.0 / div))
               .Values
            ;

            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;

            rapi.RenderMesh(__instance.GetField<MeshRef>("meshRef"));
            prog.Stop();

            return false;
        }
    }

    // The new position should only be set on the client receiving it. This breaks interpolation otherwise.
    // For teleporting.
    [HarmonyPatch(typeof(Entity), "OnReceivedServerPacket")]
    public static class ReceivedPacketPostfix
    {
        [HarmonyPrefix]
        public static bool Prefix(Entity __instance, int packetid, byte[] data)
        {
            if (packetid == 1)
            {
                Vec3d vec3d = SerializerUtil.Deserialize<Vec3d>(data);
                if (__instance == (__instance.World as ClientMain).Player.Entity)
                {
                    __instance.Pos.SetPos(vec3d);
                }
                __instance.ServerPos.SetPos(vec3d);
                __instance.World.BlockAccessor.MarkBlockDirty(vec3d.AsBlockPos);
                return false;
            }

            EnumHandling handled = EnumHandling.PassThrough;
            foreach (EntityBehavior behavior in __instance.SidedProperties.Behaviors)
            {
                behavior.OnReceivedServerPacket(packetid, data, ref handled);
                if (handled == EnumHandling.PreventSubsequent)
                {
                    break;
                }
            }

            return false;
        }
    }

    // Nametags and some positions break if the position is set here. I tried calling renderframe after received server pos in interpolation but it didn't help.
    // ServerPos is handled in interpolation.
    // Introduce an "interpolatable" field to entity.
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

            // Position not set automatically if the entity has interpolation. I'd rather not check this every time.
            if (handled == EnumHandling.PassThrough && !__instance.HasBehavior<EntityInterpolation>())
            {
                __instance.Pos.SetFrom(__instance.ServerPos);
            }

            return false;
        }
    }

    // Patch this one AI Task to also try to get step height from the new physics if you're not overriding.
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

    // Replace projectile and stone with new behavior if you're not overriding.
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