using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

/// <summary>
/// Client-side player physics.
/// </summary>
public class EntityPlayerPhysics : EntityControlledPhysics, IRenderer
{
    public IPlayer player;
    public EntityPlayer entityPlayer;

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
            // Remote on server.
            remote = false;
        }

        // If the controller of the player.
        if (!remote)
        {
            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");
            SetModules();
        }

        stepHeight = attributes["stepHeight"].AsFloat(0.6f);

        JsonObject physics = properties?.Attributes?["physics"];
        for (int i = 0; i < physicsModules.Count; i++)
        {
            physicsModules[i].Initialize(physics, entity);
        }

        sneakTestCollisionbox = entity.CollisionBox.Clone().OmniNotDownGrowBy(-0.1f);
        sneakTestCollisionbox.Y2 /= 2;

        smoothStepping = !remote;

        if (remote)
        {
            if (capi == null)
            {
                lastReceived = sapi.World.ElapsedMilliseconds;
            }
        }
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

    public void OnReceivedClientPos()
    {
        if (!remote) return;

        float dt = (sapi.World.ElapsedMilliseconds - lastReceived) / 1000f;
        lastReceived = sapi.World.ElapsedMilliseconds;

        HandleRemote(dt);
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        if (!remote) return;

        float dt = capi.World.Player.Entity.WatchedAttributes.GetFloat("lastDelta");

        HandleRemote(dt);
    }

    public void HandleRemote(float dt)
    {
        player ??= entityPlayer.Player;
        if (player == null) return;

        if (nPos == null) nPos.Set(entity.SidedPos);

        float dtFactor = dt * 60;

        lPos.SetFrom(nPos);
        nPos.Set(entity.SidedPos);

        lPos.Motion.X = (nPos.X - lPos.X) / dtFactor;
        lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor;
        lPos.Motion.Z = (nPos.Z - lPos.Z) / dtFactor;

        EntityAgent agent = entity as EntityAgent;
        if (agent?.MountedOn != null)
        {
            entity.Swimming = false;
            entity.OnGround = false;

            entity.SidedPos.Motion.X = 0;
            entity.SidedPos.Motion.Y = 0;
            entity.SidedPos.Motion.Z = 0;
            return;
        }

        entity.SidedPos.Motion.Set(lPos.Motion);

        prevPos.Set(lPos);

        SetState(lPos, dt);

        // Apply gravity then set collision.
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
        if (double.IsNaN(entity.SidedPos.Y)) return;

        player ??= entityPlayer.Player;
        if (player == null) return;

        EntityControls controls = ((EntityAgent)entity).Controls;
        EntityPos pos = entity.SidedPos;

        prevPos.Set(pos);

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

        SetState(pos, dt);
        SetPlayerControls(pos, controls, dt);
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
            if (!entity.Swimming && !controls.Gliding) entityPlayer.WalkPitch = 0;
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

    /// <summary>
    /// Do physics every frame on the client.
    /// </summary>
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (capi.IsGamePaused) return;

        // Unregister the entity if it isn't the player.
        if (capi.World.Player.Entity != entity)
        {
            remote = true;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            physicsModules.Clear();
            smoothStepping = false;
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
        }

        entity.PhysicsUpdateWatcher?.Invoke(accum, prevPos);

        AfterPhysicsTick(dt);
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