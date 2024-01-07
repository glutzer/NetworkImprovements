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
        if (entity.Api is ICoreClientAPI capi) this.capi = capi;
        if (entity.Api is ICoreServerAPI sapi) this.sapi = sapi;

        entityPlayer = entity as EntityPlayer;

        if (entity.Api.Side == EnumAppSide.Client)
        {
            //Client player initializes before anything else
            if (this.capi.World.Player == null || this.capi.World.Player.Entity == entity)
            {
                remote = false;
            }
        }

        if (!remote)
        {
            this.capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");
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
            if (this.capi != null)
            {
                lastReceived = this.capi.InWorldEllapsedMilliseconds;
            }
            else
            {
                lastReceived = this.sapi.World.ElapsedMilliseconds;
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
        HandleRemote(sapi.World.ElapsedMilliseconds);
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        if (!remote) return;
        HandleRemote(capi.InWorldEllapsedMilliseconds);
    }

    public void HandleRemote(long ellapsedMs)
    {
        player ??= entityPlayer.Player;
        if (player == null) return;

        if (nPos == null) nPos.Set(entity.SidedPos);

        float dt = (lastReceived - ellapsedMs) / 1000f;
        float dtFactor = dt * 60;
        lastReceived = ellapsedMs;

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

        lPos.SetPos(nPos);

        EntityControls controls = ((EntityAgent)entity).Controls;

        ApplyTests(lPos, controls, dt);
    }

    public override void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;
        if (double.IsNaN(entity.SidedPos.Y)) return;

        player ??= entityPlayer.Player;
        if (player == null) return;

        EntityControls controls = ((EntityAgent)entity).Controls;
        EntityPos pos = entity.SidedPos;

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
        ApplyTests(pos, controls, dt);

        if (controls.Gliding) //Might need to do this on both sides
        {
            if (entity.Collided || entity.FeetInLiquid || !entity.Alive || player.WorldData.FreeMove)
            {
                controls.GlideSpeed = 0;
                controls.Gliding = false;
                controls.IsFlying = false;
                entityPlayer.WalkPitch = 0;
            }
        }

        entity.PhysicsUpdateWatcher?.Invoke(accum, prevPos);
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
                else //Slowly revert player to upright position if feet touched the bottom of water
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

    //60/s client-side updates
    public float accum = 0;
    public float interval = 1 / 60f;

    /// <summary>
    /// Do physics every frame on the client.
    /// </summary>
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (capi.IsGamePaused) return;

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