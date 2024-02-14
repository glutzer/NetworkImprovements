using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using Vintagestory.Server;

// Client-side player physics.
public class EntityPlayerPhysics : EntityControlledPhysics, IRenderer, IRemotePhysics
{
    public IPlayer player;

    public ServerPlayer serverPlayer;
    public EntityPlayer entityPlayer;

    public int posVersion = 0;

    public ClientMain clientMain;
    public UDPNetwork udpNetwork;

    public EntityPlayerPhysics(Entity entity) : base(entity)
    {
        
    }

    public Vec3d lastGoodPos = new();

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        entityPlayer = entity as EntityPlayer;

        Init();
        SetProperties(properties, attributes);

        if (entity.Api.Side == EnumAppSide.Client)
        {
            clientMain = (ClientMain)capi.World;

            smoothStepping = true;

            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");

            udpNetwork = capi.ModLoader.GetModSystem<NIM>().udpNetwork;
        }
        else
        {
            EnumHandling handling = EnumHandling.Handled;
            OnReceivedServerPos(true, ref handling);
        }

        entity.PhysicsUpdateWatcher?.Invoke(0, entity.SidedPos.XYZ);
    }

    public override void SetModules()
    {
        physicsModules.Add(new PModuleWind());
        physicsModules.Add(new PModuleOnGround());
        physicsModules.Add(new PModulePlayerInLiquid(entityPlayer));
        physicsModules.Add(new PModulePlayerInAir());
        physicsModules.Add(new PModuleGravity());
        physicsModules.Add(new PModuleMotionDrag());
        physicsModules.Add(new PModuleKnockback());
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        //int tickDiff = entity.Attributes.GetInt("tickDiff", 1);
        //HandleRemotePhysics(clientInterval * tickDiff, isTeleport);
    }

    public new void OnReceivedClientPos(int version, int tickDiff)
    {
        serverPlayer ??= entityPlayer.Player as ServerPlayer;
        entity.ServerPos.SetFrom(entity.Pos);

        if (version > previousVersion)
        {
            previousVersion = version;
            HandleRemotePhysics(clientInterval, true);
            return;
        }

        HandleRemotePhysics(clientInterval, false);
    }

    public new void HandleRemotePhysics(float dt, bool isTeleport)
    {
        player ??= entityPlayer.Player;

        if (player == null) return;

        if (nPos == null)
        {
            nPos = new();
            nPos.Set(entity.ServerPos);
        }

        float dtFactor = dt * 60;

        lPos.SetFrom(nPos);
        nPos.Set(entity.ServerPos);

        // Set the last pos to be the same as the next pos when teleporting.
        if (isTeleport)
        {
            lPos.SetFrom(nPos);

            if (sapi != null)
            {
                if (lastValid == null)
                {
                    lastValid = new();
                    lastValid.SetFrom(lPos);
                }
                else if (!reconciling)
                {
                    lastValid.SetFrom(lPos);
                }

                reconciling = false;
            }
        }

        lPos.Motion.X = (nPos.X - lPos.X) / dtFactor;
        lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor;
        lPos.Motion.Z = (nPos.Z - lPos.Z) / dtFactor;

        if (lPos.Motion.Length() > 20) lPos.Motion.Set(0, 0, 0);

        // Anti-cheat.
        if (sapi != null)
        {
            if (lastValid == null)
            {
                lastValid = new();
                lastValid.SetFrom(lPos);
            }

            lastValid.Motion.X = (nPos.X - lastValid.X) / dtFactor;
            lastValid.Motion.Y = (nPos.Y - lastValid.Y) / dtFactor;
            lastValid.Motion.Z = (nPos.Z - lastValid.Z) / dtFactor;

            double motionLength = lastValid.Motion.Length();

            if (motionLength > 1.2 && player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                Reconcile(lastValid.XYZ);
                return;
            }
        }

        // Set client/server motion.
        entity.Pos.Motion.Set(lPos.Motion);
        entity.ServerPos.Motion.Set(lPos.Motion);

        EntityAgent agent = entity as EntityAgent;
        if (agent?.MountedOn != null)
        {
            entity.Swimming = false;
            entity.OnGround = false;

            if (capi != null)
            {
                entity.Pos.SetPos(agent.MountedOn.MountPosition);
            }

            entity.ServerPos.Motion.X = 0;
            entity.ServerPos.Motion.Y = 0;
            entity.ServerPos.Motion.Z = 0;
            return;
        }

        // Set pos for triggering events.
        entity.Pos.SetFrom(entity.ServerPos);

        SetState(lPos, dt);

        // No-clip detection.
        if (sapi != null)
        {
            collisionTester.ApplyTerrainCollision(entity, lastValid, dtFactor, ref newPos, 0, 0);

            double difference = newPos.DistanceTo(nPos);

            if (difference > 0.2)
            {
                Reconcile(lastValid.XYZ);
            }
            else
            {
                lastValid.SetFrom(newPos);
            }
        }

        RemoteMotionAndCollision(lPos, dtFactor);
        ApplyTests(lPos, ((EntityAgent)entity).Controls, dt, true);
    }

    public EntityPos lastValid;
    public bool reconciling = false;

    public void Reconcile(Vec3d pos)
    {
        reconciling = true;
        entity.TeleportToDouble(pos.X, pos.Y, pos.Z);
    }

    // Main client physics tick called every frame.
    public override void OnPhysicsTick(float dt)
    {
        SimPhysics(dt, entity.SidedPos);
    }

    public void SimPhysics(float dt, EntityPos pos)
    {
        if (entity.State != EnumEntityState.Active) return;
        player ??= entityPlayer.Player;
        if (player == null) return;

        EntityControls controls = ((EntityAgent)entity).Controls;

        // Set previous pos to be used for camera callback.
        prevPos.Set(pos);

        SetState(pos, dt);
        SetPlayerControls(pos, controls, dt);

        // If mounted on something, set position to it and return.
        EntityAgent agent = entity as EntityAgent;
        if (agent?.MountedOn != null)
        {
            entity.Swimming = false;
            entity.OnGround = false;

            pos.SetPos(agent.MountedOn.MountPosition);

            pos.Motion.X = 0;
            pos.Motion.Y = 0;
            pos.Motion.Z = 0;
            return;
        }

        MotionAndCollision(pos, controls, dt);
        ApplyTests(pos, controls, dt, false);

        // Attempt to stop gliding/flying.
        if (controls.Gliding)
        {
            if (entity.Collided || entity.FeetInLiquid || !entity.Alive || player.WorldData.FreeMove)
            {
                controls.GlideSpeed = 0;
                controls.Gliding = false;
                controls.IsFlying = false;
                entityPlayer.WalkPitch = 0;
            }
        }
        else
        {
            controls.GlideSpeed = 0;
        }
    }

    public void SetPlayerControls(EntityPos pos, EntityControls controls, float dt)
    {
        IClientWorldAccessor clientWorld = entity.World as IClientWorldAccessor;
        controls.IsFlying = player.WorldData.FreeMove || (clientWorld != null && clientWorld.Player.ClientId != player.ClientId);
        controls.NoClip = player.WorldData.NoClip;
        controls.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier;

        if (controls.Gliding)
        {
            controls.IsFlying = true;
        }

        if ((controls.TriesToMove || controls.Gliding) && player is IClientPlayer clientPlayer)
        {
            float prevYaw = pos.Yaw;
            pos.Yaw = (entity.Api as ICoreClientAPI).Input.MouseYaw;

            if (entity.Swimming || controls.Gliding)
            {
                float prevPitch = pos.Pitch;
                pos.Pitch = clientPlayer.CameraPitch;
                controls.CalcMovementVectors(pos, dt);
                pos.Yaw = prevYaw;
                pos.Pitch = prevPitch;
            }
            else
            {
                controls.CalcMovementVectors(pos, dt);
                pos.Yaw = prevYaw;
            }

            float desiredYaw = (float)Math.Atan2(controls.WalkVector.X, controls.WalkVector.Z) - GameMath.PIHALF;
            float yawDist = GameMath.AngleRadDistance(entityPlayer.WalkYaw, desiredYaw);

            entityPlayer.WalkYaw += GameMath.Clamp(yawDist, -6 * dt * GlobalConstants.OverallSpeedMultiplier, 6 * dt * GlobalConstants.OverallSpeedMultiplier);
            entityPlayer.WalkYaw = GameMath.Mod(entityPlayer.WalkYaw, GameMath.TWOPI);

            if (entity.Swimming || controls.Gliding)
            {
                float desiredPitch = -(float)Math.Sin(pos.Pitch);
                float pitchDist = GameMath.AngleRadDistance(entityPlayer.WalkPitch, desiredPitch);
                entityPlayer.WalkPitch += GameMath.Clamp(pitchDist, -2 * dt * GlobalConstants.OverallSpeedMultiplier, 2 * dt * GlobalConstants.OverallSpeedMultiplier);
                entityPlayer.WalkPitch = GameMath.Mod(entityPlayer.WalkPitch, GameMath.TWOPI);
            }
            else
            {
                entityPlayer.WalkPitch = 0;
            }
        }
        else
        {
            if (!entity.Swimming && !controls.Gliding)
            {
                entityPlayer.WalkPitch = 0;
            }
            else if (entity.OnGround && entityPlayer.WalkPitch != 0)
            {
                if (entityPlayer.WalkPitch < 0.01f || entityPlayer.WalkPitch > GameMath.TWOPI - 0.01f)
                {
                    entityPlayer.WalkPitch = 0;
                }
                else // Slowly revert player to upright position if feet touched the bottom of water.
                {   
                    entityPlayer.WalkPitch = GameMath.Mod(entityPlayer.WalkPitch, GameMath.TWOPI);
                    entityPlayer.WalkPitch -= GameMath.Clamp(entityPlayer.WalkPitch, 0, 1.2f * dt * GlobalConstants.OverallSpeedMultiplier);

                    if (entityPlayer.WalkPitch < 0) entityPlayer.WalkPitch = 0;
                }
            }

            float prevYaw = pos.Yaw;
            controls.CalcMovementVectors(pos, dt);
            pos.Yaw = prevYaw;
        }
    }

    // 60/s client-side updates.
    public float accum = 0;
    public float interval = 1 / 60f;
    public int currentTick;

    // Do physics every frame on the client.
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (capi.IsGamePaused) return;

        // Unregister the entity if it isn't the player.
        if (capi.World.Player.Entity != entity)
        {
            smoothStepping = false;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            udpNetwork = null;
            return;
        }

        accum += dt;

        if (accum > 0.5)
        {
            accum = 0;
        }

        //IMountable mount = entityPlayer.MountedOn;
        //IMountableSupplier mountSupplier = mount?.MountSupplier;
        //IPhysicsTickable tickable = (mountSupplier as Entity)?.SidedProperties.Behaviors.Find(b => b is IPhysicsTickable) as IPhysicsTickable;

        while (accum >= interval)
        {
            OnPhysicsTick(interval);
            //tickable?.OnPhysicsTick(interval);

            accum -= interval;
            currentTick++;

            // Send position every 4 ticks.
            if (currentTick % 4 == 0)
            {
                if (clientMain.GetField<bool>("Spawned") && clientMain.EntityPlayer.Alive)
                {
                    udpNetwork.SendPlayerPacket();
                    //if (mountSupplier != null) udpNetwork.SendMountPacket(mountSupplier as Entity);
                }
            }

            AfterPhysicsTick(interval);
            //tickable?.OnPhysicsTick(interval);
        }

        // For camera, lerps from prevPos to current pos by 1 + accum.
        entity.PhysicsUpdateWatcher?.Invoke(accum, prevPos);
        //(mountSupplier as Entity)?.PhysicsUpdateWatcher?.Invoke(accum, prevPos);
    }

    public double RenderOrder => 1;

    public int RenderRange => 9999;

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        capi?.Event.UnregisterRenderer(this, EnumRenderStage.Before);
    }

    public void Dispose()
    {

    }
}