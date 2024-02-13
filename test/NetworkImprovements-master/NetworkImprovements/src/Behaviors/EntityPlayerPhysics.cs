using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;

// Client-side player physics.
public class EntityPlayerPhysics : EntityControlledPhysics, IRenderer
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
        if (entity.Api is ICoreClientAPI) capi = entity.Api as ICoreClientAPI;
        if (entity.Api is ICoreServerAPI) sapi = entity.Api as ICoreServerAPI;

        entityPlayer = entity as EntityPlayer;

        if (entity.Api.Side == EnumAppSide.Client)
        {
            clientMain = (ClientMain)capi.World;

            // Remote on server. First render frame on client checks if it's a local player.
            remote = false;
        }

        stepHeight = attributes["stepHeight"].AsFloat(0.6f);
        sneakTestCollisionbox = entity.CollisionBox.Clone().OmniNotDownGrowBy(-0.1f);
        sneakTestCollisionbox.Y2 /= 2;
        smoothStepping = !remote;

        // If the controller of the player.
        if (!remote)
        {
            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");
            udpNetwork = capi.ModLoader.GetModSystem<NIM>().udpNetwork;
        }

        SetModules();

        JsonObject physics = properties?.Attributes?["physics"];
        for (int i = 0; i < physicsModules.Count; i++)
        {
            physicsModules[i].Initialize(physics, entity);
        }

        if (remote)
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
        physicsModules.Add(new PlayerInLiquid(entityPlayer));
        physicsModules.Add(new PlayerInAir());
        physicsModules.Add(new PModuleGravity());
        physicsModules.Add(new PModuleMotionDrag());
        physicsModules.Add(new PModuleKnockback());
    }

    public void OnReceivedClientPos(int version, int tickDiff)
    {
        if (!remote) return;

        serverPlayer ??= entityPlayer.Player as ServerPlayer;

        // Both normal and server pos are now set to the received pos.
        // At the very least there should be movement.
        entity.ServerPos.SetFrom(entity.Pos);

        bool isTeleport = version > posVersion;

        if (isTeleport)
        {
            posVersion = version;
        }

        HandleRemote(updateInterval, isTeleport);
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        if (!remote) return;

        HandleRemote(updateInterval, isTeleport);
    }

    public void HandleRemote(float dt, bool isTeleport)
    {
        player ??= entityPlayer.Player;

        if (player == null) return;

        if (nPos == null)
        {
            nPos = new();
            nPos.Set(entity.ServerPos); // Should be sided pos?
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

        if (lPos.Motion.Length() > 20)
        {
            lPos.Motion.Set(0, 0, 0);
        }

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

        prevPos.Set(lPos);

        SetState(lPos, dt);

        // No-clip detection.
        if (sapi != null)
        {
            collisionTester.ApplyTerrainCollision(entity, lastValid, dtFactor, ref outPos, 0, 0);

            double difference = outPos.DistanceTo(nPos);

            if (difference > 0.2)
            {
                Reconcile(lastValid.XYZ);
            }
            else
            {
                lastValid.SetFrom(outPos);
            }
        }

        // Apply gravity then set collision.
        double gravityStrength = (1 / 60f * dtFactor) + Math.Max(0, -0.015f * lPos.Motion.Y * dtFactor);
        lPos.Motion.Y -= gravityStrength;
        collisionTester.ApplyTerrainCollision(entity, lPos, dtFactor, ref outPos, 0, 0);
        bool falling = lPos.Motion.Y < 0;
        entity.OnGround = entity.CollidedVertically && falling;
        lPos.Motion.Y += gravityStrength;

        lPos.SetPos(nPos);

        EntityControls controls = ((EntityAgent)entity).Controls;

        ApplyTests(lPos, controls, dt);
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

        ApplyMotion(pos, controls, dt);
        ApplyTests(pos, controls, dt);

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
            remote = true;
            smoothStepping = false;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            physicsModules.Clear();
            udpNetwork = null;
            return;
        }

        accum += dt;

        if (accum > 0.5)
        {
            accum = 0;
        }

        while (accum >= interval)
        {
            OnPhysicsTick(interval);
            accum -= interval;
            currentTick++;

            // Send position every 4 ticks.
            if (currentTick % 4 == 0)
            {
                if (clientMain.GetField<bool>("Spawned") && clientMain.EntityPlayer.Alive)
                {
                    udpNetwork.SendPlayerPacket();
                }
            }

            AfterPhysicsTick(interval);
        }

        // For camera, lerps from prevPos to current pos by 1 + accum.
        entity.PhysicsUpdateWatcher?.Invoke(accum, prevPos);
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