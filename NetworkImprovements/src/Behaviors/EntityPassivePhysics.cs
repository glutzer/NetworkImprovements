using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

public class EntityPassivePhysics : PhysicsBehaviorBase, IPhysicsTickable, IRemotePhysics
{
    public bool Ticking { get; set; }

    public Action<float> OnPhysicsTickCallback;

    public EntityPassivePhysics(Entity entity) : base(entity)
    {
    }

    // State info.
    public Vec3d prevPos = new();
    public double motionBeforeY = 0;
    public bool feetInLiquidBefore = false;
    public bool onGroundBefore = false;
    public bool swimmingBefore = false;
    public bool collidedBefore = false;

    public void SetState(EntityPos pos)
    {
        prevPos.Set(pos);
        motionBeforeY = pos.Motion.Y;
        onGroundBefore = entity.OnGround;
        feetInLiquidBefore = entity.FeetInLiquid;
        swimmingBefore = entity.Swimming;
        collidedBefore = entity.Collided;
    }

    // Output of collision tester.
    public Vec3d newPos = new();

    public double groundDragValue = 0.7f;
    public double waterDragValue = GlobalConstants.WaterDrag;
    public double airDragValue = GlobalConstants.AirDragAlways;
    public double gravityPerSecond = GlobalConstants.GravityPerSecond;

    public void SetProperties(JsonObject attributes)
    {
        waterDragValue = 1 - ((1 - waterDragValue) * attributes["waterDragFactor"].AsDouble(1));

        JsonObject airDragFactor = attributes["airDragFactor"];
        double airDrag = airDragFactor.Exists ? airDragFactor.AsDouble(1) : attributes["airDragFallingFactor"].AsDouble(1);
        airDragValue = 1 - ((1 - airDragValue) * airDrag);
        if (entity.WatchedAttributes.HasAttribute("airDragFactor")) airDragValue = 1 - ((1 - GlobalConstants.AirDragAlways) * (float)entity.WatchedAttributes.GetDouble("airDragFactor"));

        groundDragValue = 0.3 * attributes["groundDragFactor"].AsDouble(1);

        gravityPerSecond *= attributes["gravityFactor"].AsDouble(1);
        if (entity.WatchedAttributes.HasAttribute("gravityFactor")) gravityPerSecond = GlobalConstants.GravityPerSecond * (float)entity.WatchedAttributes.GetDouble("gravityFactor");
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        Init();
        SetProperties(attributes);

        if (entity.Api.Side == EnumAppSide.Server)
        {
            NIM.AddPhysicsTickable(entity.Api, this);
        }
        else
        {
            EnumHandling handling = EnumHandling.Handled;
            OnReceivedServerPos(true, ref handling);
        }

        if (capi != null) Ticking = true;
    }

    public override void OnReceivedServerPos(bool isTeleport, ref EnumHandling handled)
    {
        //int tickDiff = entity.Attributes.GetInt("tickDiff", 1);
        //HandleRemotePhysics(clientInterval * tickDiff, isTeleport);
    }

    public void OnReceivedClientPos(int version, int tickDiff)
    {
        if (version > previousVersion)
        {
            previousVersion = version;
            HandleRemotePhysics(clientInterval, true);
            return;
        }

        HandleRemotePhysics(clientInterval, false);
    }

    public void HandleRemotePhysics(float dt, bool isTeleport)
    {
        if (nPos == null)
        {
            nPos = new();
            nPos.Set(entity.ServerPos);
        }

        float dtFactor = dt * 60;

        lPos.SetFrom(nPos);
        nPos.Set(entity.ServerPos);

        if (isTeleport) lPos.SetFrom(nPos);

        lPos.Motion.X = (nPos.X - lPos.X) / dtFactor;
        lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor;
        lPos.Motion.Z = (nPos.Z - lPos.Z) / dtFactor;

        if (lPos.Motion.Length() > 20) lPos.Motion.Set(0, 0, 0);

        // Set client motion.
        entity.Pos.Motion.Set(lPos.Motion);
        entity.ServerPos.Motion.Set(lPos.Motion);

        // Set pos for triggering events (interpolation overrides this).
        entity.Pos.SetFrom(entity.ServerPos);

        SetState(lPos);
        RemoteMotionAndCollision(lPos, dtFactor);
        ApplyTests(lPos, dt);
    }

    public void RemoteMotionAndCollision(EntityPos pos, float dtFactor)
    {
        double gravityStrength = (gravityPerSecond / 60f * dtFactor) + Math.Max(0, -0.015f * pos.Motion.Y * dtFactor);
        pos.Motion.Y -= gravityStrength;
        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref newPos, 0, collisionYExtra);
        bool falling = pos.Motion.Y < 0;
        entity.OnGround = entity.CollidedVertically && falling;
        pos.Motion.Y += gravityStrength;
        pos.SetPos(nPos);
    }

    public void MotionAndCollision(EntityPos pos, float dt)
    {
        float dtFactor = 60 * dt;

        // Apply drag from block below entity.
        if (onGroundBefore)
        {
            if (!feetInLiquidBefore)
            {
                Block belowBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y - 0.05f), (int)pos.Z, BlockLayersAccess.Solid);
                pos.Motion.X *= 1 - (groundDragValue * belowBlock.DragMultiplier);
                pos.Motion.Z *= 1 - (groundDragValue * belowBlock.DragMultiplier);
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
            double gravityStrength = (gravityPerSecond / 60f * dtFactor) + Math.Max(0, -0.015f * pos.Motion.Y * dtFactor);

            if (entity.Swimming)
            {
                // Above 0 => floats.
                // Below 0 => sinks.
                float boyancy = GameMath.Clamp(1 - (entity.MaterialDensity / insideFluid.MaterialDensity), -1, 1);

                Block aboveFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);
                float waterY = (int)pos.Y + (insideFluid.LiquidLevel / 8f) + (aboveFluid.IsLiquid() ? 9 / 8f : 0);

                // 0 => at swim line.
                // 1 => completely submerged.
                float submergedLevel = waterY - (float)pos.Y;
                float swimLineSubmergedness = GameMath.Clamp(submergedLevel - (entity.SelectionBox.Y2 - (float)entity.SwimmingOffsetY), 0, 1);

                double boyancyStrength = GameMath.Clamp(60 * boyancy * swimLineSubmergedness, -1.5f, 1.5f) - 1;

                double waterDrag = GameMath.Clamp((100 * Math.Abs(pos.Motion.Y * dtFactor)) - 0.02f, 1, 1.25f);

                pos.Motion.Y += gravityStrength * boyancyStrength;
                pos.Motion.Y /= waterDrag;
            }
            else
            {
                pos.Motion.Y -= gravityStrength;
            }
        }

        double nextX = (pos.Motion.X * dtFactor) + pos.X;
        double nextY = (pos.Motion.Y * dtFactor) + pos.Y;
        double nextZ = (pos.Motion.Z * dtFactor) + pos.Z;

        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref newPos, 0, collisionYExtra);

        // Clamp inside the world.
        if (entity.World.BlockAccessor.IsNotTraversable((int)nextX, (int)pos.Y, (int)pos.Z)) newPos.X = pos.X;
        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)nextY, (int)pos.Z)) newPos.Y = pos.Y;
        if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)pos.Y, (int)nextZ)) newPos.Z = pos.Z;

        // Finally set position.
        pos.SetPos(newPos);

        // Stop motion if collided.
        if ((nextX < newPos.X && pos.Motion.X < 0) || (nextX > newPos.X && pos.Motion.X > 0)) pos.Motion.X = 0;
        if ((nextY < newPos.Y && pos.Motion.Y < 0) || (nextY > newPos.Y && pos.Motion.Y > 0)) pos.Motion.Y = 0;
        if ((nextZ < newPos.Z && pos.Motion.Z < 0) || (nextZ > newPos.Z && pos.Motion.Z > 0)) pos.Motion.Z = 0;
    }

    public void ApplyTests(EntityPos pos, float dt)
    {
        float dtFactor = 60 * dt;

        bool falling = pos.Motion.Y <= 0;
        entity.OnGround = entity.CollidedVertically && falling;

        Block fluidBlock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);
        entity.FeetInLiquid = fluidBlock.MatterState == EnumMatterState.Liquid;
        entity.InLava = fluidBlock.LiquidCode == "lava";

        if (entity.FeetInLiquid)
        {
            Block aboveBlockFluid = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);
            float waterY = (int)pos.Y + (fluidBlock.LiquidLevel / 8f) + (aboveBlockFluid.IsLiquid() ? 9 / 8f : 0);
            float submergedLevel = waterY - (float)pos.Y;
            float swimlineSubmergedness = submergedLevel - (entity.SelectionBox.Y2 - (float)entity.SwimmingOffsetY);
            entity.Swimming = swimlineSubmergedness > 0;
        }
        else
        {
            entity.Swimming = false;
        }

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

        if (entity.OnGround)
        {
            entity.PositionBeforeFalling.Set(newPos);
        }

        if (GlobalConstants.OutsideWorld(pos.X, pos.Y, pos.Z, entity.World.BlockAccessor))
        {
            entity.DespawnReason = new EntityDespawnData() { Reason = EnumDespawnReason.Death, DamageSourceForDeath = new DamageSource() { Source = EnumDamageSource.Fall } };
            return;
        }

        // Entity was inside all of these blocks this tick, call events.
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

        // Invoke callbacks. There is no accumulation left because this is fixed tick.
        OnPhysicsTickCallback?.Invoke(0);
        entity.PhysicsUpdateWatcher?.Invoke(0, prevPos);
    }

    public void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active || !Ticking) return;

        EntityPos pos = entity.ServerPos;

        // If entity is moving 6 blocks per second test 10 times. Needs dynamic adjustment this is overkill.
        int loops = pos.Motion.Length() > 0.1 ? 10 : 1;
        float newDt = dt / loops;

        for (int i = 0; i < loops; i++)
        {
            SetState(pos);
            MotionAndCollision(pos, newDt);
            ApplyTests(pos, newDt);
        }

        entity.Pos.SetFrom(pos);
    }

    public void AfterPhysicsTick(float dt)
    {

    }

    volatile int serverPhysicsTickDone = 0;
    public ref int FlagTickDone { get => ref serverPhysicsTickDone; }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (sapi != null) NIM.RemovePhysicsTickable(entity.Api, this);
    }

    public override string PropertyName()
    {
        return "entitypassivephysics";
    }
}