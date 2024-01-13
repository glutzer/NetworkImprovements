using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;

// Client-side player physics.
public class EntityPlayerPhysics : EntityControlledPhysics, IRenderer
{
    public IPlayer player;
    public ServerPlayer serverPlayer;
    public EntityPlayer entityPlayer;
    public long lastReceivedPosition;
    public int posVersion = 0;

    public ClientMain clientMain;

    public UDPNetwork udpNetwork;

    public EntityPlayerPhysics(Entity entity) : base(entity)
    {
        
    }

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
        else
        {
            // Can be removed if interval is sent in player position packet.
            lastReceivedPosition = sapi.World.ElapsedMilliseconds;
        }

        stepHeight = attributes["stepHeight"].AsFloat(0.6f);
        sneakTestCollisionbox = entity.CollisionBox.Clone().OmniNotDownGrowBy(-0.1f);
        sneakTestCollisionbox.Y2 /= 2;
        smoothStepping = !remote;

        // If the controller of the player.
        if (!remote)
        {
            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");
            SetModules();
            udpNetwork = capi.ModLoader.GetModSystem<NIM>().udpNetwork;
            tick = entity.WatchedAttributes.GetInt("ct");
        }

        JsonObject physics = properties?.Attributes?["physics"];
        for (int i = 0; i < physicsModules.Count; i++)
        {
            physicsModules[i].Initialize(physics, entity);
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

    public float updateInterval = 1 / 15f;

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

        HandleRemote(updateInterval * tickDiff, isTeleport);
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        if (!remote) return;

        //entity.Pos.SetFrom(entity.ServerPos);

        HandleRemote(updateInterval * entity.WatchedAttributes.GetInt("tickDiff"), isTeleport);
    }

    public void HandleRemote(float dt, bool isTeleport)
    {
        player ??= entityPlayer.Player;

        if (player == null) return;

        //if (nPos == null) nPos.Set(entity.SidedPos);
        if (nPos == null) nPos.Set(entity.ServerPos);

        float dtFactor = dt * 60;

        lPos.SetFrom(nPos);

        //nPos.Set(entity.SidedPos);
        nPos.Set(entity.ServerPos);

        // Set the last pos to be the same as the next pos when teleporting.
        if (isTeleport)
        {
            lPos.SetFrom(nPos);
        }

        lPos.Motion.X = (nPos.X - lPos.X) / dtFactor;
        lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor;
        lPos.Motion.Z = (nPos.Z - lPos.Z) / dtFactor;

        // I think this is set when interpolating anyways?
        EntityAgent agent = entity as EntityAgent;
        if (agent?.MountedOn != null)
        {
            entity.Swimming = false;
            entity.OnGround = false;

            if (capi != null)
            {
                entity.SidedPos.SetPos(agent.MountedOn.MountPosition);
            }

            entity.SidedPos.Motion.X = 0;
            entity.SidedPos.Motion.Y = 0;
            entity.SidedPos.Motion.Z = 0;
            return;
        }

        entity.SidedPos.Motion.Set(lPos.Motion);

        prevPos.Set(lPos);

        SetState(lPos, dt);

        // Apply gravity then set collision. If collision needs to happen remotely do it here.
        // Notice there's no fall damage on the server because collision never happens remotely.
        /*
        double gravityStrength = 1 / 60f * dtFactor + Math.Max(0, -0.015f * lPos.Motion.Y * dtFactor);
        lPos.Motion.Y -= gravityStrength;
        collisionTester.ApplyTerrainCollision(entity, lPos, dtFactor, ref outPos, 0, 0);
        bool falling = lPos.Motion.Y < 0;
        entity.OnGround = entity.CollidedVertically && falling;
        lPos.Motion.Y += gravityStrength;
        */

        lPos.SetPos(nPos);

        EntityControls controls = ((EntityAgent)entity).Controls;

        ApplyTests(lPos, controls, dt);
    }

    // Main client physics tick called every frame.
    public override void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        player ??= entityPlayer.Player;

        if (player == null) return;

        // Weird NAN bug.
        //if (double.IsNaN(entity.SidedPos.Y)) return;

        EntityPos pos = entity.SidedPos;
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
    public int tick;

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

        if (accum > 5000)
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
                    udpNetwork.SendPlayerPacket(tick);
                    tick++;
                }
            }

            // This should be in here right?
            entity.PhysicsUpdateWatcher?.Invoke(accum, prevPos);
            AfterPhysicsTick(dt);
        }
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