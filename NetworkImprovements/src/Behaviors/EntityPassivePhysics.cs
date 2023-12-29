using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

/// <summary>
/// Passive physics rewritten to not be render-based but tick based on both the server and client. Interpolate position will handle the smoothing.
/// </summary>
public class EntityPassivePhysics : EntityBehavior, IPhysicsTickable
{
    [ThreadStatic]
    public static CachingCollisionTester collisionTester;

    public Vec3d outPos = new();
    public Vec3d prevPos = new();
    public Vec3d moveDelta = new();
    public Vec3d nextPos = new();

    //Drag values that will affect how the entity moves
    public double waterDragValue = GlobalConstants.WaterDrag;
    public double airDragValue = GlobalConstants.AirDragAlways;
    public double gravityPerSecond = GlobalConstants.GravityPerSecond;

    public double groundDragFactor = 0.7f;
    
    public float collisionYExtra = 1f;

    public bool remote = true;

    /// <summary>
    /// Callback set by entity after physics have ticked.
    /// </summary>
    public Action<float> OnPhysicsTickCallback;

    public bool isMountable;

    /// <summary>
    /// Data for minimal client physics.
    /// </summary>
    public EntityPos lPos;
    public Vec3d nPos = new();

    public EntityPassivePhysics(Entity entity) : base(entity)
    {
        isMountable = entity is IMountable || entity is IMountableSupplier;
    }

    /// <summary>
    /// Called initially upon loading.
    /// </summary>
    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        //Water drag
        waterDragValue = 1 - (1 - waterDragValue) * attributes["waterDragFactor"].AsDouble(1);

        //Air drag
        JsonObject airDragFactor = attributes["airDragFactor"];
        double airDrag = airDragFactor.Exists ? airDragFactor.AsDouble(1) : attributes["airDragFallingFactor"].AsDouble(1);
        airDragValue = 1 - (1 - airDragValue) * airDrag;

        //Another air drag value located in watched attributes instead of regular attributes
        if (entity.WatchedAttributes.HasAttribute("airDragFactor")) airDragValue = 1 - (1 - GlobalConstants.AirDragAlways) * (float)entity.WatchedAttributes.GetDouble("airDragFactor");

        //Ground drag
        groundDragFactor = 0.3 * attributes["groundDragFactor"].AsDouble(1);

        //Gravity
        gravityPerSecond *= attributes["gravityFactor"].AsDouble(1);

        //Another gravity value located in watched attributes instead of regular attributes
        if (entity.WatchedAttributes.HasAttribute("gravityFactor")) gravityPerSecond = GlobalConstants.GravityPerSecond * (float)entity.WatchedAttributes.GetDouble("gravityFactor");

        NIM.AddPhysicsTickable(entity.Api, this);

        if (entity.Api.Side == EnumAppSide.Server)
        {
            remote = false;
        }
    }

    /// <summary>
    /// Set motion and next position on client.
    /// </summary>
    public void RemoteMotion(float dt)
    {
        nPos.X = entity.Pos.X;
        nPos.Y = entity.Pos.Y;
        nPos.Z = entity.Pos.Z;

        float dtFactor = 60 * dt;

        lPos.Motion.X = (nPos.X - lPos.X) / dtFactor;
        lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor;
        lPos.Motion.Z = (nPos.Z - lPos.Z) / dtFactor;

        //Apply constant gravity on the remote game so things continue to collide with the ground
        if (lPos.Motion.Y <= double.Epsilon) lPos.Motion.Y = Math.Min(lPos.Motion.Y, -0.01);

        entity.SidedPos.Motion.Set(lPos.Motion);

        prevPos.Set(lPos.X, lPos.Y, lPos.Z);
        nextPos.Set(nPos.X, nPos.Y, nPos.Z);
    }

    /// <summary>
    /// Set motion and next position on server.
    /// </summary>
    public void MainMotion(EntityPos pos, float dt, bool feetInLiquidBefore, bool onGroundBefore, bool swimmingBefore)
    {
        float dtFactor = 60 * dt;

        //Apply ground drag
        if (onGroundBefore)
        {
            if (!feetInLiquidBefore)
            {
                Block belowBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y - 0.05f), (int)pos.Z, BlockLayersAccess.Solid);
                pos.Motion.X *= 1 - groundDragFactor * belowBlock.DragMultiplier;
                pos.Motion.Z *= 1 - groundDragFactor * belowBlock.DragMultiplier;
            }
        }

        //Apply both water and air drag
        Block insideFluid = null;
        if (feetInLiquidBefore || swimmingBefore)
        {
            pos.Motion *= Math.Pow(waterDragValue, dt * 33);

            insideFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);

            //Add push vector to motion
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
            pos.Motion *= Math.Pow(airDragValue, dt * 33); //Multiply motion by air drag if not in water
        }

        //Apply gravity
        //Water drag applied twice here?
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

        nextPos.Set(pos.X + pos.Motion.X * dtFactor, pos.Y + pos.Motion.Y * dtFactor, pos.Z + pos.Motion.Z);
    }

    public void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        collisionTester ??= new CachingCollisionTester();
        collisionTester.NewTick();

        float dtFactor = 60 * dt;

        EntityPos pos;
        if (remote)
        {
            lPos ??= new()
            {
                X = entity.SidedPos.X,
                Y = entity.SidedPos.Y,
                Z = entity.SidedPos.Z,
            };
            pos = lPos;
        }
        else
        {
            pos = entity.SidedPos;
        }

        double motionBeforeY = pos.Motion.Y;
        bool feetInLiquidBefore = entity.FeetInLiquid;
        bool onGroundBefore = entity.OnGround;
        bool swimmingBefore = entity.Swimming;
        bool onCollidedBefore = entity.Collided;

        if (remote)
        {
            RemoteMotion(dt);
        }
        else
        {
            MainMotion(pos, dt, feetInLiquidBefore, onGroundBefore, swimmingBefore);
        }

        bool falling = pos.Motion.Y < 0;

        //Apply terrain collision taking motion into account
        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref outPos, 0, collisionYExtra);

        entity.OnGround = entity.CollidedVertically && falling;

        //If the position will be outside of the map don't change the position at all
        if (entity.World.BlockAccessor.IsNotTraversable((int)nextPos.X, (int)pos.Y, (int)pos.Z)) outPos.X = pos.X;
        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)nextPos.Y, (int)pos.Z)) outPos.Y = pos.Y;
        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)pos.Y, (int)nextPos.Z)) outPos.Z = pos.Z;

        //Set position to collided adjustment position
        pos.SetPos(outPos);

        //If collided with something set the motion to 0
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

        //Trigger all events
        if (!onCollidedBefore && entity.Collided)
        {
            entity.OnCollided();
        }

        if (!onGroundBefore && entity.OnGround)
        {
            entity.OnFallToGround(motionBeforeY);
        }

        //|| (!swimmingBefore && entity.Swimming)
        if (!feetInLiquidBefore && entity.FeetInLiquid)
        {
            entity.OnCollideWithLiquid();
        }

        if ((swimmingBefore && !entity.Swimming && !entity.FeetInLiquid) || (feetInLiquidBefore && !entity.FeetInLiquid && !entity.Swimming))
        {
            entity.OnExitedLiquid();
        }

        //Set position before falling for fall damage
        if (!falling || entity.OnGround)
        {
            entity.PositionBeforeFalling.Set(outPos);
        }

        //Check if the entity is outside of the world and despawn it
        if (GlobalConstants.OutsideWorld(pos.X, pos.Y, pos.Z, entity.World.BlockAccessor))
        {
            entity.DespawnReason = new EntityDespawnData() { Reason = EnumDespawnReason.Death, DamageSourceForDeath = new DamageSource() { Source = EnumDamageSource.Fall } };
            return;
        }

        //Trigger events for every block the collision box is inside
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

        //Invoke callbacks
        OnPhysicsTickCallback?.Invoke(dtFactor);
        entity.PhysicsUpdateWatcher?.Invoke(dtFactor, prevPos);

        //The last tested pos is now the location the entity is at this tick
        if (remote)
        {
            lPos.X = entity.SidedPos.X;
            lPos.Y = entity.SidedPos.Y;
            lPos.Z = entity.SidedPos.Z;
        }
    }

    public void AfterPhysicsTick(float dt)
    {
        //No post events
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