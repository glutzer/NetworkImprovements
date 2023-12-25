using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

/// <summary>
/// Client-side player physics.
/// Calls every frame instead of the physics controller.
/// Client-authorative fall damage.
/// </summary>
public class EntityPlayerPhysics : EntityControlledPhysics, IRenderer
{
    public EntityPlayer entityPlayer;
    public ICoreClientAPI capi;

    public EntityPlayerPhysics(Entity entity) : base(entity)
    {
        entityPlayer = entity as EntityPlayer;
        capi = entity.Api as ICoreClientAPI;
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        (entity.Api as ICoreClientAPI)?.Event.RegisterRenderer(this, EnumRenderStage.Before, "playerphysics");
        base.Initialize(properties, attributes);
    }

    public override void SetModules()
    {
        physicsModules.Add(new PModuleOnGround());
        physicsModules.Add(new PlayerInLiquid(entityPlayer));
        physicsModules.Add(new PlayerInAir());
        physicsModules.Add(new PModuleGravity());
        physicsModules.Add(new PModuleMotionDrag());
    }

    //Player movement is fixed interval like vanilla for now
    public float accum = 0;

    public override void OnPhysicsTick(float dt)
    {
        traversed.Clear();

        //Update wind value every 20 ticks
        tickCounter++;
        if (tickCounter % 100 == 0)
        {
            if (tickCounter == 500) tickCounter = 0;
        }

        accum += dt;
        if (accum > 1)
        {
            accum = 1;
        }

        //Always self now
        float frameTime = 1 / 60f;
        smoothStepping = true;

        if (accum >= frameTime)   // Only do this code section if we will actually be ticking TickEntityPhysicsPre
        {
            SetupKnockbackValues();

            collisionTester ??= new CachingCollisionTester();
            collisionTester.NewTick();

            while (accum >= frameTime)
            {
                FixedIntervalTick(entity, frameTime);
                accum -= frameTime;
            }
        }

        entity.PhysicsUpdateWatcher?.Invoke(accum, prevPos);

        //Glider stuff?
        IPlayer player = entityPlayer.Player;
        EntityControls controls = entityPlayer.Controls;
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
    }

    public void FixedIntervalTick(Entity entity, float dt)
    {
        prevPos.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        EntityControls controls = entityPlayer.Controls;
        IClientPlayer player = entityPlayer.Player as IClientPlayer;

        //More glider stuff
        if (player != null)
        {
            IClientWorldAccessor clientWorld = entity.World as IClientWorldAccessor;

            controls.IsFlying = player.WorldData.FreeMove || (clientWorld != null && clientWorld.Player.ClientId != player.ClientId);
            controls.NoClip = player.WorldData.NoClip;
            controls.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier;

            if (player != null && controls.Gliding) controls.IsFlying = true;
        }

        EntityPos pos = entity.Pos;

        //If trying to move or gliding
        if (controls.TriesToMove || controls.Gliding)
        {
            float prevYaw = pos.Yaw;
            pos.Yaw = capi.Input.MouseYaw;

            if (entity.Swimming || controls.Gliding)
            {
                float prevPitch = pos.Pitch;
                pos.Pitch = player.CameraPitch;
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
        else //If not moving
        {
            if (!entity.Swimming && !controls.Gliding) entityPlayer.WalkPitch = 0;
            else if (entity.OnGround && entityPlayer.WalkPitch != 0)
            {
                if (entityPlayer.WalkPitch < 0.01f || entityPlayer.WalkPitch > GameMath.TWOPI - 0.01f) entityPlayer.WalkPitch = 0;
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

        TickEntityPhysics(pos, controls, dt);
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (capi.IsGamePaused) return;

        OnPhysicsTick(dt);
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