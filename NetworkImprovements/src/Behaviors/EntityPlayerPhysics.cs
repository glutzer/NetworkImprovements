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
/// Calls every frame instead of the physics controller.
/// Client-authorative fall damage.
/// </summary>
public class EntityPlayerPhysics : EntityControlledPhysics, IRenderer
{
    public IPlayer player;
    public EntityPlayer entityPlayer;
    public ICoreClientAPI capi;

    public EntityPlayerPhysics(Entity entity) : base(entity)
    {
        entityPlayer = entity as EntityPlayer;
        capi = entity.Api as ICoreClientAPI;
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        SetModules();

        stepHeight = attributes["stepHeight"].AsFloat(0.6f);

        JsonObject physics = properties?.Attributes?["physics"];
        for (int i = 0; i < physicsModules.Count; i++)
        {
            physicsModules[i].Initialize(physics, entity);
        }

        sneakTestCollisionbox = entity.CollisionBox.Clone().OmniNotDownGrowBy(-0.1f);
        sneakTestCollisionbox.Y2 /= 2;

        if (entity.Api.Side == EnumAppSide.Client)
        {
            if (capi.World.Player == null || capi.World.Player.Entity == entity)
            {
                remote = false;
            }
        }

        if (remote)
        {
            NIM.AddPhysicsTickable(api, this);
        }
        else
        {
            (entity.Api as ICoreClientAPI)?.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");
        }

        smoothStepping = !remote;
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

    //Called 30 times/s remotely. Every frame on client.
    public override void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        player ??= entityPlayer.Player;

        if (player == null) return;

        if (double.IsNaN(entity.SidedPos.Y)) return;

        if (entity.World.Side == EnumAppSide.Server && ((IServerPlayer)player).ConnectionState != EnumClientState.Playing) return;

        float dtFactor = dt * 60;

        traversed.Clear();

        collisionTester ??= new CachingCollisionTester();
        collisionTester.NewTick();

        //Adjust collision box of dead entities
        if (!entity.Alive) AdjustCollisionBoxToAnimation(dtFactor);

        //Get controls
        EntityControls controls = ((EntityAgent)entity).Controls;

        //Pre-physics
        IClientWorldAccessor clientWorld = entity.World as IClientWorldAccessor;
        controls.IsFlying = player.WorldData.FreeMove || (clientWorld != null && clientWorld.Player.ClientId != player.ClientId);
        controls.NoClip = player.WorldData.NoClip;
        controls.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier;

        if (controls.Gliding)
        {
            controls.IsFlying = true;
        }

        //Get pos, last pos on server and client pos on client
        EntityPos pos;
        if (remote)
        {
            lPos ??= new()
            {
                X = entity.SidedPos.X,
                Y = entity.SidedPos.Y,
                Z = entity.SidedPos.Z
            };
            pos = lPos;
        }
        else
        {
            pos = entity.SidedPos;
        }

        //If trying to glide or move update controls
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
            else if (entity.OnGround && entityPlayer.WalkPitch != 0 && player is IClientPlayer)
            {
                if (entityPlayer.WalkPitch < 0.01f || entityPlayer.WalkPitch > GameMath.TWOPI - 0.01f)
                {
                    entityPlayer.WalkPitch = 0;
                }
                else
                {   //Slowly revert player to upright position if feet touched the bottom of water
                    entityPlayer.WalkPitch = GameMath.Mod(entityPlayer.WalkPitch, GameMath.TWOPI);
                    entityPlayer.WalkPitch -= GameMath.Clamp(entityPlayer.WalkPitch, 0, 1.2f * dt * GlobalConstants.OverallSpeedMultiplier);
                    if (entityPlayer.WalkPitch < 0) entityPlayer.WalkPitch = 0;
                }
            }
            float prevYaw = pos.Yaw;
            controls.CalcMovementVectors(pos, dt);
            pos.Yaw = prevYaw;
        }

        if (remote)
        {
            RemoteMotion(dt);
        }
        else
        {
            MainMotion(pos, controls, dt);
        }

        //If the agent is mounted on something, set the position to what it's mounted on and return
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

        CollideAndMove(pos, controls, dt, dtFactor);

        if (player != null && controls.Gliding)
        {
            if (entity.Collided || entity.FeetInLiquid || !entity.Alive || player.WorldData.FreeMove)
            {
                controls.GlideSpeed = 0;
                controls.Gliding = false;
                controls.IsFlying = false;
                entityPlayer.WalkPitch = 0;
            }
        }

        //The last tested pos is now the location the entity is at this tick
        if (remote)
        {
            if (prevPos.X != entity.SidedPos.X || prevPos.Y != entity.SidedPos.Y || prevPos.Z != entity.SidedPos.Z)
            {
                lPos.X = entity.SidedPos.X;
                lPos.Y = entity.SidedPos.Y;
                lPos.Z = entity.SidedPos.Z;
            }
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