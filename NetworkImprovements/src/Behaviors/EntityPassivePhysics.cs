using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

/// <summary>
/// Passive physics rewritten to not be render-based but tick based on both the server and client.
/// </summary>
public class EntityPassivePhysics : EntityBehavior, IPhysicsTickable
{
    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    public EntityPassivePhysics(Entity entity) : base(entity)
    {

    }

    /// <summary>
    /// Callback set by entity after physics have ticked.
    /// </summary>
    public Action<float> OnPhysicsTickCallback;

    [ThreadStatic]
    public static CachingCollisionTester collisionTester;

    public Vec3d prevPos = new();
    public Vec3d moveDelta = new();
    public Vec3d nextPos = new();
    public Vec3d outPos = new();

    public double groundDragValue = 0.7f;
    public double waterDragValue = GlobalConstants.WaterDrag;
    public double airDragValue = GlobalConstants.AirDragAlways;
    public double gravityPerSecond = GlobalConstants.GravityPerSecond;
    
    public float collisionYExtra = 1f;

    public bool remote = true;

    public bool isMountable;

    /// <summary>
    /// Data for minimal client physics.
    /// Use last received position to calculate motion.
    /// Will only be used on the thread handling network.
    /// </summary>
    public EntityPos lPos = new();
    public Vec3d nPos = new();

    /// <summary>
    /// Called when entity spawns.
    /// </summary>
    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        // Set water drag.
        waterDragValue = 1 - (1 - waterDragValue) * attributes["waterDragFactor"].AsDouble(1);

        // Set air drag.
        JsonObject airDragFactor = attributes["airDragFactor"];
        double airDrag = airDragFactor.Exists ? airDragFactor.AsDouble(1) : attributes["airDragFallingFactor"].AsDouble(1);
        airDragValue = 1 - (1 - airDragValue) * airDrag;
        if (entity.WatchedAttributes.HasAttribute("airDragFactor")) airDragValue = 1 - (1 - GlobalConstants.AirDragAlways) * (float)entity.WatchedAttributes.GetDouble("airDragFactor");

        // Set ground drag.
        groundDragValue = 0.3 * attributes["groundDragFactor"].AsDouble(1);

        // Gravity.
        gravityPerSecond *= attributes["gravityFactor"].AsDouble(1);
        if (entity.WatchedAttributes.HasAttribute("gravityFactor")) gravityPerSecond = GlobalConstants.GravityPerSecond * (float)entity.WatchedAttributes.GetDouble("gravityFactor");

        isMountable = entity is IMountable || entity is IMountableSupplier;

        // Check if this should be executed remotely.
        if (entity.Api.Side == EnumAppSide.Server)
        {
            remote = false;
        }

        // Tick on logical side.
        if (!remote)
        {
            NIM.AddPhysicsTickable(entity.Api, this);
        }

        if (entity.Api is ICoreClientAPI capi) this.capi = capi;
        if (entity.Api is ICoreServerAPI sapi) this.sapi = sapi;
    }

    public float updateInterval = 1 / 15f;

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        if (!remote) return;

        if (nPos == null) nPos.Set(entity.SidedPos);

        float dt = updateInterval;
        float dtFactor = dt * 60;

        lPos.SetFrom(nPos);
        nPos.Set(entity.SidedPos);

        lPos.Motion.X = (nPos.X - lPos.X) / dtFactor;
        lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor;
        lPos.Motion.Z = (nPos.Z - lPos.Z) / dtFactor;

        if (lPos.Motion.Length() > 100)
        {
            lPos.Motion.Set(0, 0, 0);
        }

        entity.SidedPos.Motion.Set(lPos.Motion);

        prevPos.Set(lPos);

        SetState(lPos);

        // Apply gravity then set collision.
        double gravityStrength = gravityPerSecond / 60f * dtFactor + Math.Max(0, -0.015f * lPos.Motion.Y * dtFactor);
        lPos.Motion.Y -= gravityStrength;
        collisionTester.ApplyTerrainCollision(entity, lPos, dtFactor, ref outPos, 0, collisionYExtra);
        bool falling = lPos.Motion.Y < 0;
        entity.OnGround = entity.CollidedVertically && falling;
        lPos.Motion.Y += gravityStrength;

        // Set the last pos to the next pos to simulate motion being applied.
        lPos.SetPos(nPos);

        ApplyTests(lPos, dt);
    }

    private double motionBeforeY = 0;
    private bool feetInLiquidBefore = false;
    private bool onGroundBefore = false;
    private bool swimmingBefore = false;
    private bool collidedBefore = false;

    public void SetState(EntityPos pos)
    {
        collisionTester ??= new CachingCollisionTester();
        collisionTester.NewTick();

        // Set a new collision tick and the values before motion was calculated to do tests.
        motionBeforeY = pos.Motion.Y;
        onGroundBefore = entity.OnGround;
        feetInLiquidBefore = entity.FeetInLiquid;
        swimmingBefore = entity.Swimming;
        collidedBefore = entity.Collided;
    }

    public void ApplyMotion(EntityPos pos, float dt)
    {
        float dtFactor = 60 * dt;

        // Apply drag from block below entity.
        if (onGroundBefore)
        {
            if (!feetInLiquidBefore)
            {
                Block belowBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y - 0.05f), (int)pos.Z, BlockLayersAccess.Solid);
                pos.Motion.X *= 1 - groundDragValue * belowBlock.DragMultiplier;
                pos.Motion.Z *= 1 - groundDragValue * belowBlock.DragMultiplier;
            }
        }

        // Apply water drag and push vector inside liquid, and air drag outside of liquid.
        Block insideFluid = null;
        if (feetInLiquidBefore || swimmingBefore)
        {
            pos.Motion *= Math.Pow(waterDragValue, dt * 33);

            insideFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);

            if (feetInLiquidBefore)
            {
                Vec3d pushVector = insideFluid.PushVector;
                if (pushVector != null)
                {
                    float pushStrength = 0.3f * 1000f / GameMath.Clamp(entity.MaterialDensity, 750, 2500) * dtFactor;

                    pos.Motion.Add(
                        pushVector.X * pushStrength,
                        pushVector.Y * pushStrength,
                        pushVector.Z * pushStrength
                    );
                }
            }
        }
        else
        {
            pos.Motion *= (float)Math.Pow(airDragValue, dt * 33);
        }

        // Apply gravity.
        if (entity.ApplyGravity)
        {
            double gravityStrength = gravityPerSecond / 60f * dtFactor + Math.Max(0, -0.015f * pos.Motion.Y * dtFactor);

            if (entity.Swimming)
            {
                //Above 0 => floats
                //Below 0 => sinks
                float boyancy = GameMath.Clamp(1 - entity.MaterialDensity / insideFluid.MaterialDensity, -1, 1);

                Block aboveFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);
                float waterY = (int)pos.Y + insideFluid.LiquidLevel / 8f + (aboveFluid.IsLiquid() ? 9 / 8f : 0);

                //0 => at swim line
                //1 => completely submerged
                float submergedLevel = waterY - (float)pos.Y;
                float swimLineSubmergedness = GameMath.Clamp(submergedLevel - (entity.SelectionBox.Y2 - (float)entity.SwimmingOffsetY), 0, 1);

                double boyancyStrength = GameMath.Clamp(60 * boyancy * swimLineSubmergedness, -1.5f, 1.5f) - 1;

                double waterDrag = GameMath.Clamp(100 * Math.Abs(pos.Motion.Y * dtFactor) - 0.02f, 1, 1.25f);

                pos.Motion.Y += gravityStrength * boyancyStrength;
                pos.Motion.Y /= waterDrag;
            }
            else
            {
                pos.Motion.Y -= gravityStrength;
            }
        }

        // Set next position after motion applied.
        moveDelta.Set(pos.Motion.X * dtFactor, pos.Motion.Y * dtFactor, pos.Motion.Z * dtFactor);
        nextPos.Set(pos.X + moveDelta.X, pos.Y + moveDelta.Y, pos.Z + moveDelta.Z);

        // Apply terrain collision with the current motion with the new position being at outPos.
        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref outPos, 0, collisionYExtra);

        // Clamp entity inside allowed world.
        if (entity.World.BlockAccessor.IsNotTraversable((int)nextPos.X, (int)pos.Y, (int)pos.Z)) outPos.X = pos.X;
        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)nextPos.Y, (int)pos.Z)) outPos.Y = pos.Y;
        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)pos.Y, (int)nextPos.Z)) outPos.Z = pos.Z;

        // Test if on the ground now.
        bool falling = pos.Motion.Y < 0;
        entity.OnGround = entity.CollidedVertically && falling;

        // Finally set position.
        pos.SetPos(outPos);

        // Stop motion if collided.
        if ((nextPos.X < outPos.X && pos.Motion.X < 0) || (nextPos.X > outPos.X && pos.Motion.X > 0))
        {
            pos.Motion.X = 0;
        }
        if ((nextPos.Y < outPos.Y && pos.Motion.Y < 0) || (nextPos.Y > outPos.Y && pos.Motion.Y > 0))
        {
            pos.Motion.Y = 0;
        }
        if ((nextPos.Z < outPos.Z && pos.Motion.Z < 0) || (nextPos.Z > outPos.Z && pos.Motion.Z > 0))
        {
            pos.Motion.Z = 0;
        }
    }

    public void ApplyTests(EntityPos pos, float dt)
    {
        float dtFactor = 60 * dt;

        Vec3i posInt = pos.XYZInt;
        Block fluid = entity.World.BlockAccessor.GetBlock(posInt.X, posInt.Y, posInt.Z, BlockLayersAccess.Fluid);
        entity.FeetInLiquid = fluid.MatterState == EnumMatterState.Liquid;
        entity.InLava = fluid.LiquidCode == "lava";

        if (entity.FeetInLiquid)
        {
            Block aboveBlockFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);

            float waterY = (int)pos.Y + fluid.LiquidLevel / 8f + (aboveBlockFluid.IsLiquid() ? 9 / 8f : 0);
            float submergedLevel = waterY - (float)pos.Y;

            //0 => at swim line
            //1 => completely submerged
            float swimlineSubmergedness = submergedLevel - (entity.SelectionBox.Y2 - (float)entity.SwimmingOffsetY);

            entity.Swimming = swimlineSubmergedness > 0;
        }
        else
        {
            entity.Swimming = false;
        }

        // Trigger events here.
        if (!onGroundBefore && entity.OnGround)
        {
            entity.OnFallToGround(motionBeforeY);
        }

        if (!feetInLiquidBefore && entity.FeetInLiquid)
        {
            entity.OnCollideWithLiquid();
        }

        if ((swimmingBefore && !entity.Swimming && !entity.FeetInLiquid) || (feetInLiquidBefore && !entity.FeetInLiquid && !entity.Swimming))
        {
            entity.OnExitedLiquid();
        }

        if (!collidedBefore && entity.Collided)
        {
            entity.OnCollided();
        }

        // Set last position the entity was currently on the ground.
        if (entity.OnGround)
        {
            entity.PositionBeforeFalling.Set(outPos);
        }

        // Kill entity outside world.
        if (GlobalConstants.OutsideWorld(pos.X, pos.Y, pos.Z, entity.World.BlockAccessor))
        {
            entity.DespawnReason = new EntityDespawnData() { Reason = EnumDespawnReason.Death, DamageSourceForDeath = new DamageSource() { Source = EnumDamageSource.Fall } };
            return;
        }

        // Trigger onEntityInside for each block the entity is currently in.
        Cuboidd entityBox = collisionTester.entityBox;
        int xMax = (int)entityBox.X2;
        int yMax = (int)entityBox.Y2;
        int zMax = (int)entityBox.Z2;
        int zMin = (int)entityBox.Z1;
        for (int y = (int)entityBox.Y1; y <= yMax; y++)
        {
            for (int x = (int)entityBox.X1; x <= xMax; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    collisionTester.tmpPos.Set(x, y, z);
                    entity.World.BlockAccessor.GetBlock(x, y, z).OnEntityInside(entity.World, entity, collisionTester.tmpPos);
                }
            }
        }

        // Invoke callbacks.
        OnPhysicsTickCallback?.Invoke(dt);
        entity.PhysicsUpdateWatcher?.Invoke(dt, prevPos);
    }

    public void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        EntityPos pos = entity.SidedPos;

        prevPos.Set(pos);

        SetState(pos);
        ApplyMotion(pos, dt);
        ApplyTests(pos, dt);
    }

    public void AfterPhysicsTick(float dt)
    {

    }

    /// <summary>
    /// This is for the physics manager to load balance the tickables.
    /// </summary>
    volatile int serverPhysicsTickDone = 0;
    public ref int FlagTickDone { get => ref serverPhysicsTickDone; }

    /// <summary>
    /// Unregister tickable.
    /// </summary>
    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        NIM.RemovePhysicsTickable(entity.Api, this);
    }

    public override string PropertyName()
    {
        return "entitypassivephysics";
    }
}