using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

/// <summary>
/// Passive physics rewritten to not be render-based but tick based on both the server and client. Interpolate position will handle the smoothing.
/// </summary>
public class EntityPassivePhysics : EntityBehavior, PhysicsTickable
{
    [ThreadStatic]
    public static CachingCollisionTester collisionTester;

    //Use a seperate pos for simulating physics on the client
    EntityPos clientPos;
    public float clientAccum = 0;
    public float syncInterval = 2;

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

    /// <summary>
    /// Callback set by entity after physics have ticked.
    /// </summary>
    public Action<float> OnPhysicsTickCallback;

    public bool isMountable;

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
    }

    public void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;
        
        //Use the current real position to simulate physics on the client
        if (clientAccum == 0)
        {
            clientPos = entity.Pos.Copy();
        }
        clientAccum += dt;
        if (clientAccum > syncInterval)
        {
            clientAccum = 0;
        }

        EntityPos pos;
        if (entity.Api.Side == EnumAppSide.Server)
        {
            pos = entity.ServerPos;
        }
        else
        {
            pos = clientPos;
        }

        float dtFactor = 60 * dt;

        collisionTester ??= new CachingCollisionTester();
        collisionTester.NewTick();

        prevPos.Set(pos.X, pos.Y, pos.Z);

        double motionBeforeY = pos.Motion.Y;
        bool feetInLiquidBefore = entity.FeetInLiquid;
        bool onGroundBefore = entity.OnGround;
        bool swimmingBefore = entity.Swimming;
        bool onCollidedBefore = entity.Collided;

        //Apply ground drag
        //Motion is multiplied by 1 - the drag multiplier of the block type it's on
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
        Block inBlockFluid = null;
        if (feetInLiquidBefore || swimmingBefore)
        {
            pos.Motion *= Math.Pow(waterDragValue, dt * 33); //Multiply motion by fluid drag value

            inBlockFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);

            //Add push vector to motion
            if (feetInLiquidBefore)
            {
                Vec3d pushVector = inBlockFluid.PushVector;
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
            //Multiply pos by air drag if not in water
            pos.Motion *= Math.Pow(airDragValue, dt * 33);
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
                float boyancy = GameMath.Clamp(1 - entity.MaterialDensity / inBlockFluid.MaterialDensity, -1, 1);

                Block aboveBlockFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);
                float waterY = (int)pos.Y + inBlockFluid.LiquidLevel / 8f + (aboveBlockFluid.IsLiquid() ? 9 / 8f : 0);

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

        //After all motion calculated, set delta
        moveDelta.Set(pos.Motion.X * dtFactor, pos.Motion.Y * dtFactor, pos.Motion.Z * dtFactor);

        //Next pos is the current pos + the delta
        nextPos.Set(pos.X + moveDelta.X, pos.Y + moveDelta.Y, pos.Z + moveDelta.Z);

        bool falling = pos.Motion.Y < 0;

        //Collision tester takes entity motion into account
        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref outPos, 0, collisionYExtra);

        //If the position will be outside of the map don't change the position at all
        //Wall effect
        if (entity.World.BlockAccessor.IsNotTraversable((int)nextPos.X, (int)pos.Y, (int)pos.Z)) outPos.X = pos.X;

        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)nextPos.Y, (int)pos.Z)) outPos.Y = pos.Y;

        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)pos.Y, (int)nextPos.Z)) outPos.Z = pos.Z;

        entity.OnGround = entity.CollidedVertically && falling;

        //outPos is the value after terrain collision has been applied
        pos.SetPos(outPos);

        //Example: If nextPos is higher than the collision adjusted pos and currently going up, set the motion to 0 as collision has happened in that direction
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

        //Get block at new pos
        Vec3i posInt = pos.XYZInt;
        Block fluid = entity.World.BlockAccessor.GetBlock(posInt.X, posInt.Y, posInt.Z, BlockLayersAccess.Fluid);

        //Set if the entity is in liquid and lava
        entity.FeetInLiquid = fluid.MatterState == EnumMatterState.Liquid;
        entity.InLava = fluid.LiquidCode == "lava";

        //Get block above
        if (entity.FeetInLiquid)
        {
            Block aboveBlockFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);

            //Get exact float the top level of the fluid is at
            float waterY = (int)pos.Y + fluid.LiquidLevel / 8f + (aboveBlockFluid.IsLiquid() ? 9 / 8f : 0);

            //How much above or below the water level we are
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

        if ((!entity.Swimming && !feetInLiquidBefore && entity.FeetInLiquid) || (!entity.FeetInLiquid && !swimmingBefore && entity.Swimming))
        {
            entity.OnCollideWithLiquid();
        }

        //Set position before falling for fall damage
        if (!falling || entity.OnGround)
        {
            entity.PositionBeforeFalling.Set(outPos);
        }

        //Check if the entity is outside of the world and despawns it
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

        //Set motion values on client
        if (entity.Api.Side == EnumAppSide.Client)
        {
            entity.Pos.Motion.X = pos.Motion.X;
            entity.Pos.Motion.Y = pos.Motion.Y;
            entity.Pos.Motion.Z = pos.Motion.Z;
        }

        //Invoke callbacks
        OnPhysicsTickCallback?.Invoke(dtFactor);
        entity.PhysicsUpdateWatcher?.Invoke(dtFactor, prevPos);
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