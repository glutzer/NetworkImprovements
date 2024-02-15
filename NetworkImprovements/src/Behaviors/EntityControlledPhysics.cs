using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

// New version of server-side controlled physics.
public class EntityControlledPhysics : PhysicsBehaviorBase, IPhysicsTickable, IRemotePhysics
{
    public bool Ticking { get; set; }

    public const double collisionboxReductionForInsideBlocksCheck = 0.009;
    public float stepHeight = 0.6f;
    public bool smoothStepping = false;

    public List<PModule> physicsModules = new();
    public List<PModule> customModules = new();

    public Vec3d newPos = new();

    public BlockPos tmpPos = new();
    public Cuboidd entityBox = new();
    public List<FastVec3i> traversed = new(4);
    public IComparer<FastVec3i> fastVec3iComparer = new FastVec3iComparer();

    public Vec3d moveDelta = new();

    public Vec3d prevPos = new();
    public double prevYMotion;
    public bool onGroundBefore;
    public bool feetInLiquidBefore;
    public bool swimmingBefore;

    public void SetState(EntityPos pos, float dt)
    {
        float dtFactor = dt * 60;

        prevPos.Set(pos);
        prevYMotion = pos.Motion.Y;
        onGroundBefore = entity.OnGround;
        feetInLiquidBefore = entity.FeetInLiquid;
        swimmingBefore = entity.Swimming;

        traversed.Clear();
        if (!entity.Alive) AdjustCollisionBoxToAnimation(dtFactor);
    }

    public EntityControlledPhysics(Entity entity) : base(entity)
    {
        
    }

    public virtual void SetModules()
    {
        physicsModules.Add(new PModuleWind());
        physicsModules.Add(new PModuleOnGround());
        physicsModules.Add(new PModuleInLiquid());
        physicsModules.Add(new PModuleInAir());
        physicsModules.Add(new PModuleGravity());
        physicsModules.Add(new PModuleMotionDrag());
        physicsModules.Add(new PModuleKnockback());
    }

    public void SetProperties(EntityProperties properties, JsonObject attributes)
    {
        stepHeight = attributes["stepHeight"].AsFloat(0.6f);
        sneakTestCollisionbox = entity.CollisionBox.Clone().OmniNotDownGrowBy(-0.1f);
        sneakTestCollisionbox.Y2 /= 2;

        SetModules();

        JsonObject physics = properties?.Attributes?["physics"];
        for (int i = 0; i < physicsModules.Count; i++)
        {
            physicsModules[i].Initialize(physics, entity);
        }
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        Init();
        SetProperties(properties, attributes);

        if (entity.Api.Side == EnumAppSide.Server)
        {
            NIM.AddPhysicsTickable(entity.Api, this);
        }

        entity.PhysicsUpdateWatcher?.Invoke(0, entity.SidedPos.XYZ);

        // This is for entity shape renderer. Somewhere else should determine when this is set since it can be on both sides now.
        // ----------
        // ----------
        // ----------
        // ----------
        // ----------
        if (entity.Api.Side == EnumAppSide.Client)
        {
            EnumHandling handling = EnumHandling.Handled;
            OnReceivedServerPos(true, ref handling);

            entity.Attributes.RegisterModifiedListener("dmgkb", () =>
            {
                if (entity.Attributes.GetInt("dmgkb") == 1)
                {
                    kbCounter = 2;
                }
            });
        }
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

        // Set pos for triggering events (interpolation overrides this).
        entity.Pos.SetFrom(entity.ServerPos);

        SetState(lPos, dt);
        RemoteMotionAndCollision(lPos, dtFactor);
        ApplyTests(lPos, ((EntityAgent)entity).Controls, dt, true);

        // Knockback is only removed on the server in the knockback module. It needs to be set on the client so entities don't remain tilted.
        // Should always set it to 1 when taking damage instead of when it's 0 in the entity class so the timer can always get updated.
        // Entity should tilt back to normal state if it's being knocked back too.
        if (kbCounter > 0)
        {
            kbCounter -= dt;
        }
        else
        {
            kbCounter = 0;
            entity.Attributes.SetInt("dmgkb", 0);
        }
    }

    public int kbState;
    public float kbCounter = 0;

    
    public void RemoteMotionAndCollision(EntityPos pos, float dtFactor)
    {
        double gravityStrength = (1 / 60f * dtFactor) + Math.Max(0, -0.015f * pos.Motion.Y * dtFactor);
        pos.Motion.Y -= gravityStrength;
        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref newPos, 0, 0);
        bool falling = lPos.Motion.Y < 0;
        entity.OnGround = entity.CollidedVertically && falling;
        pos.Motion.Y += gravityStrength;
        pos.SetPos(nPos);
    }

    public void MotionAndCollision(EntityPos pos, EntityControls controls, float dt)
    {
        foreach (PModule physicsModule in physicsModules)
        {
            if (physicsModule.Applicable(entity, pos, controls))
            {
                physicsModule.DoApply(dt, entity, pos, controls);
            }
        }

        foreach (PModule physicsModule in customModules)
        {
            if (physicsModule.Applicable(entity, pos, controls))
            {
                physicsModule.DoApply(dt, entity, pos, controls);
            }
        }
    }

    public void ApplyTests(EntityPos pos, EntityControls controls, float dt, bool remote)
    {
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;
        float dtFactor = dt * 60;

        controls.IsClimbing = false;
        entity.ClimbingOnFace = null;
        entity.ClimbingIntoFace = null;
        if (entity.Properties.CanClimb == true)
        {
            int height = (int)Math.Ceiling(entity.CollisionBox.Y2);
            entityBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);
            for (int dy = 0; dy < height; dy++)
            {
                tmpPos.Set((int)pos.X, (int)pos.Y + dy, (int)pos.Z);
                Block inBlock = blockAccessor.GetBlock(tmpPos);
                if (!inBlock.IsClimbable(tmpPos) && !entity.Properties.CanClimbAnywhere) continue;
                Cuboidf[] collisionBoxes = inBlock.GetCollisionBoxes(blockAccessor, tmpPos);
                if (collisionBoxes == null) continue;
                for (int i = 0; i < collisionBoxes.Length; i++)
                {
                    double distance = entityBox.ShortestDistanceFrom(collisionBoxes[i], tmpPos);
                    controls.IsClimbing |= distance < entity.Properties.ClimbTouchDistance;

                    if (controls.IsClimbing)
                    {
                        entity.ClimbingOnFace = null;
                        break;
                    }
                }
            }

            if (controls.WalkVector.LengthSq() > 0.00001 && entity.Properties.CanClimbAnywhere && entity.Alive)
            {
                BlockFacing walkIntoFace = BlockFacing.FromVector(controls.WalkVector.X, controls.WalkVector.Y, controls.WalkVector.Z);
                if (walkIntoFace != null)
                {
                    tmpPos.Set((int)pos.X + walkIntoFace.Normali.X, (int)pos.Y + walkIntoFace.Normali.Y, (int)pos.Z + walkIntoFace.Normali.Z);
                    Block inBlock = blockAccessor.GetBlock(tmpPos);

                    Cuboidf[] collisionBoxes = inBlock.GetCollisionBoxes(blockAccessor, tmpPos);
                    entity.ClimbingIntoFace = (collisionBoxes != null && collisionBoxes.Length != 0) ? walkIntoFace : null;
                }
            }

            for (int i = 0; !controls.IsClimbing && i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing facing = BlockFacing.HORIZONTALS[i];
                for (int dy = 0; dy < height; dy++)
                {
                    tmpPos.Set((int)pos.X + facing.Normali.X, (int)pos.Y + dy, (int)pos.Z + facing.Normali.Z);
                    Block inBlock = blockAccessor.GetBlock(tmpPos);
                    if (!inBlock.IsClimbable(tmpPos) && !(entity.Properties.CanClimbAnywhere && entity.Alive)) continue;

                    Cuboidf[] collisionBoxes = inBlock.GetCollisionBoxes(blockAccessor, tmpPos);
                    if (collisionBoxes == null) continue;

                    for (int j = 0; j < collisionBoxes.Length; j++)
                    {
                        double distance = entityBox.ShortestDistanceFrom(collisionBoxes[j], tmpPos);
                        controls.IsClimbing |= distance < entity.Properties.ClimbTouchDistance;

                        if (controls.IsClimbing)
                        {
                            entity.ClimbingOnFace = facing;
                            entity.ClimbingOnCollBox = collisionBoxes[j];
                            break;
                        }
                    }
                }
            }
        }

        if (!remote)
        {
            if (controls.IsClimbing)
            {
                if (controls.WalkVector.Y == 0)
                {
                    pos.Motion.Y = controls.Sneak ? Math.Max(-0.07, pos.Motion.Y - 0.07) : pos.Motion.Y;
                    if (controls.Jump) pos.Motion.Y = 0.035 * dt * 60f;
                }
            }

            double nextX = (pos.Motion.X * dtFactor) + pos.X;
            double nextY = (pos.Motion.Y * dtFactor) + pos.Y;
            double nextZ = (pos.Motion.Z * dtFactor) + pos.Z;

            collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref newPos, 0, collisionYExtra);

            if (!entity.Properties.CanClimbAnywhere)
            {
                if (smoothStepping)
                {
                    controls.IsStepping = HandleSteppingOnBlocksSmooth(pos, moveDelta, dtFactor, controls);
                }
                else
                {
                    controls.IsStepping = HandleSteppingOnBlocks(pos, moveDelta, dtFactor, controls);
                }
            }

            HandleSneaking(pos, controls, dt);

            if (entity.CollidedHorizontally && !controls.IsClimbing && !controls.IsStepping && entity.Properties.Habitat != EnumHabitat.Underwater)
            {
                if (blockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 0.5), (int)pos.Z).LiquidLevel >= 7 || blockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z).LiquidLevel >= 7 || (blockAccessor.GetBlock((int)pos.X, (int)(pos.Y - 0.05), (int)pos.Z).LiquidLevel >= 7))
                {
                    pos.Motion.Y += 0.2 * dt;
                    controls.IsStepping = true;
                }
                else
                {
                    double absX = Math.Abs(pos.Motion.X);
                    double absZ = Math.Abs(pos.Motion.Z);
                    if (absX > absZ)
                    {
                        if (absZ < 0.001) pos.Motion.Z += pos.Motion.Z < 0 ? -0.0025 : 0.0025;
                    }
                    else
                    {
                        if (absX < 0.001) pos.Motion.X += pos.Motion.X < 0 ? -0.0025 : 0.0025;
                    }
                }
            }

            if (entity.World.BlockAccessor.IsNotTraversable((int)nextX, (int)pos.Y, (int)pos.Z)) newPos.X = pos.X;
            if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)nextY, (int)pos.Z)) newPos.Y = pos.Y;
            if (entity.World.BlockAccessor.IsNotTraversable((int)pos.X, (int)pos.Y, (int)nextZ)) newPos.Z = pos.Z;

            pos.SetPos(newPos);

            if ((nextX < newPos.X && pos.Motion.X < 0) || (nextX > newPos.X && pos.Motion.X > 0)) pos.Motion.X = 0;
            if ((nextY < newPos.Y && pos.Motion.Y < 0) || (nextY > newPos.Y && pos.Motion.Y > 0)) pos.Motion.Y = 0;
            if ((nextZ < newPos.Z && pos.Motion.Z < 0) || (nextZ > newPos.Z && pos.Motion.Z > 0)) pos.Motion.Z = 0;
        }

        bool falling = prevYMotion <= 0;
        entity.OnGround = entity.CollidedVertically && falling;

        float offX = entity.CollisionBox.X2 - entity.OriginCollisionBox.X2;
        float offZ = entity.CollisionBox.Z2 - entity.OriginCollisionBox.Z2;

        int posX = (int)(pos.X + offX);
        int posZ = (int)(pos.Z + offZ);

        Block blockFluid = blockAccessor.GetBlock(posX, (int)pos.Y, posZ, BlockLayersAccess.Fluid);
        Block middleWOIBlock = blockAccessor.GetBlock(posX, (int)(pos.Y + entity.SwimmingOffsetY), posZ, BlockLayersAccess.Fluid);

        entity.OnGround = (entity.CollidedVertically && falling && !controls.IsClimbing) || controls.IsStepping;
        entity.FeetInLiquid = false;

        if (blockFluid.IsLiquid())
        {
            Block aboveBlock = blockAccessor.GetBlock(posX, (int)(pos.Y + 1), posZ, BlockLayersAccess.Fluid);
            entity.FeetInLiquid = (blockFluid.LiquidLevel + (aboveBlock.LiquidLevel > 0 ? 1 : 0)) / 8f >= pos.Y - (int)pos.Y;
        }

        entity.InLava = blockFluid.LiquidCode == "lava";
        entity.Swimming = middleWOIBlock.IsLiquid();

        if (!onGroundBefore && entity.OnGround)
        {
            entity.OnFallToGround(prevYMotion);
        }

        if (!feetInLiquidBefore && entity.FeetInLiquid) entity.OnCollideWithLiquid();

        if ((swimmingBefore && !entity.Swimming && !entity.FeetInLiquid) || (feetInLiquidBefore && !entity.FeetInLiquid && !entity.Swimming)) entity.OnExitedLiquid();

        if (!falling || entity.OnGround || controls.IsClimbing) entity.PositionBeforeFalling.Set(pos);

        Cuboidd testedEntityBox = collisionTester.entityBox;
        int xMax = (int)(testedEntityBox.X2 - collisionboxReductionForInsideBlocksCheck);
        int yMax = (int)(testedEntityBox.Y2 - collisionboxReductionForInsideBlocksCheck);
        int zMax = (int)(testedEntityBox.Z2 - collisionboxReductionForInsideBlocksCheck);
        int xMin = (int)(testedEntityBox.X1 + collisionboxReductionForInsideBlocksCheck);
        int zMin = (int)(testedEntityBox.Z1 + collisionboxReductionForInsideBlocksCheck);
        for (int y = (int)(testedEntityBox.Y1 + collisionboxReductionForInsideBlocksCheck); y <= yMax; y++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    FastVec3i posTraversed = new(x, y, z);
                    int index = traversed.BinarySearch(posTraversed, fastVec3iComparer);
                    if (index < 0) index = ~index;
                    traversed.Insert(index, posTraversed);
                }
            }
        }

        entity.PhysicsUpdateWatcher?.Invoke(0, prevPos);
    }

    public virtual void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        EntityPos pos = entity.SidedPos;
        EntityControls controls = ((EntityAgent)entity).Controls;
        
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
        MotionAndCollision(pos, controls, dt);
        ApplyTests(pos, controls, dt, false);

        // For falling.
        entity.Pos.SetFrom(entity.ServerPos);
    }

    public virtual void AfterPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        if (IsBeingControlled()) return;

        // Call OnEntityInside events.
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;
        tmpPos.Set(-1, -1, -1);
        Block block = null;
        foreach (FastVec3i pos in traversed)
        {
            if (!pos.Equals(tmpPos))
            {
                tmpPos.Set(pos);
                block = blockAccessor.GetBlock(tmpPos);
            }
            if (block.Id > 0) block.OnEntityInside(entity.World, entity, tmpPos);
        }
    }

    public Cuboidf sneakTestCollisionbox = new();
    public void HandleSneaking(EntityPos pos, EntityControls controls, float dt)
    {
        if (!controls.Sneak || !entity.OnGround || pos.Motion.Y > 0) return;

        // Sneak to prevent falling off blocks.
        Vec3d testPosition = new();
        testPosition.Set(pos.X, pos.Y - (GlobalConstants.GravityPerSecond * dt), pos.Z);

        // Only apply this if the entity is on the ground in the first place.
        if (!collisionTester.IsColliding(entity.World.BlockAccessor, sneakTestCollisionbox, testPosition)) return;

        tmpPos.Set((int)pos.X, (int)pos.Y - 1, (int)pos.Z);
        Block belowBlock = entity.World.BlockAccessor.GetBlock(tmpPos);

        // Test for X.
        testPosition.Set(newPos.X, newPos.Y - (GlobalConstants.GravityPerSecond * dt), pos.Z);
        if (!collisionTester.IsColliding(entity.World.BlockAccessor, sneakTestCollisionbox, testPosition))
        {
            if (belowBlock.IsClimbable(tmpPos))
            {
                newPos.X += (pos.X - newPos.X) / 10;
            }
            else
            {
                newPos.X = pos.X;
            }
        }

        // Test for Z.
        testPosition.Set(pos.X, newPos.Y - (GlobalConstants.GravityPerSecond * dt), newPos.Z);
        if (!collisionTester.IsColliding(entity.World.BlockAccessor, sneakTestCollisionbox, testPosition))
        {
            if (belowBlock.IsClimbable(tmpPos))
            {
                newPos.Z += (pos.Z - newPos.Z) / 10;
            }
            else
            {
                newPos.Z = pos.Z;
            }
        }
    }

    public Cuboidd steppingCollisionBox = new();
    public Vec3d steppingTestVec = new();
    public Vec3d steppingTestMotion = new();

    private bool HandleSteppingOnBlocks(EntityPos pos, Vec3d moveDelta, float dtFac, EntityControls controls)
    {
        if (controls.WalkVector.X == 0 && controls.WalkVector.Z == 0) return false;

        if ((!entity.OnGround && !entity.Swimming) || entity.Properties.Habitat == EnumHabitat.Underwater) return false;

        steppingCollisionBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);
        steppingCollisionBox.Y2 = Math.Max(steppingCollisionBox.Y1 + stepHeight, steppingCollisionBox.Y2);

        Vec3d walkVec = controls.WalkVector;
        Cuboidd steppableBox = FindSteppableCollisionBox(steppingCollisionBox, moveDelta.Y, walkVec);

        if (steppableBox != null)
        {
            Vec3d testMotion = steppingTestMotion;
            testMotion.Set(moveDelta.X, moveDelta.Y, moveDelta.Z);
            if (TryStep(pos, testMotion, dtFac, steppableBox, steppingCollisionBox)) return true;

            Vec3d testVec = steppingTestVec;
            testMotion.Z = 0;
            if (TryStep(pos, testMotion, dtFac, FindSteppableCollisionBox(steppingCollisionBox, moveDelta.Y, testVec.Set(walkVec.X, walkVec.Y, 0)), steppingCollisionBox)) return true;

            testMotion.Set(0, moveDelta.Y, moveDelta.Z);
            if (TryStep(pos, testMotion, dtFac, FindSteppableCollisionBox(steppingCollisionBox, moveDelta.Y, testVec.Set(0, walkVec.Y, walkVec.Z)), steppingCollisionBox)) return true;

            return false;
        }

        return false;
    }

    public bool HandleSteppingOnBlocksSmooth(EntityPos pos, Vec3d moveDelta, float dtFac, EntityControls controls)
    {
        if (!controls.TriesToMove || (!entity.OnGround && !entity.Swimming) || entity.Properties.Habitat == EnumHabitat.Underwater) return false;

        Cuboidd entityCollisionBox = entity.CollisionBox.ToDouble();

        double max = 0.75;
        double searchBoxLength = max + (controls.Sprint ? 0.25 : controls.Sneak ? 0.05 : 0.2);

        Vec2d center = new((entityCollisionBox.X1 + entityCollisionBox.X2) / 2, (entityCollisionBox.Z1 + entityCollisionBox.Z2) / 2);
        double searchHeight = Math.Max(entityCollisionBox.Y1 + stepHeight, entityCollisionBox.Y2);
        entityCollisionBox.Translate(pos.X, pos.Y, pos.Z);

        Vec3d walkVec = controls.WalkVector.Clone();
        Vec3d walkVecNormalized = walkVec.Clone().Normalize();

        Cuboidd entitySensorBox;

        double outerX = walkVecNormalized.X * searchBoxLength;
        double outerZ = walkVecNormalized.Z * searchBoxLength;

        entitySensorBox = new Cuboidd
        {
            X1 = Math.Min(0, outerX),
            X2 = Math.Max(0, outerX),

            Z1 = Math.Min(0, outerZ),
            Z2 = Math.Max(0, outerZ),

            Y1 = entity.CollisionBox.Y1 + 0.01 - (!entity.CollidedVertically && !controls.Jump ? 0.05 : 0),

            Y2 = searchHeight
        };

        entitySensorBox.Translate(center.X, 0, center.Y);
        entitySensorBox.Translate(pos.X, pos.Y, pos.Z);

        Vec3d testVec = new();
        Vec2d testMotion = new();

        List<Cuboidd> steppableBoxes = FindSteppableCollisionboxSmooth(entityCollisionBox, entitySensorBox, moveDelta.Y, walkVec);

        if (steppableBoxes != null && steppableBoxes.Count > 0)
        {
            if (TryStepSmooth(controls, pos, testMotion.Set(walkVec.X, walkVec.Z), dtFac, steppableBoxes, entityCollisionBox)) return true;

            Cuboidd entitySensorBoxXAligned = entitySensorBox.Clone();
            if (entitySensorBoxXAligned.Z1 == pos.Z + center.Y)
            {
                entitySensorBoxXAligned.Z2 = entitySensorBoxXAligned.Z1;
            }
            else
            {
                entitySensorBoxXAligned.Z1 = entitySensorBoxXAligned.Z2;
            }

            if (TryStepSmooth(controls, pos, testMotion.Set(walkVec.X, 0), dtFac, FindSteppableCollisionboxSmooth(entityCollisionBox, entitySensorBoxXAligned, moveDelta.Y, testVec.Set(walkVec.X, walkVec.Y, 0)), entityCollisionBox)) return true;

            Cuboidd entitySensorBoxZAligned = entitySensorBox.Clone();
            if (entitySensorBoxZAligned.X1 == pos.X + center.X)
            {
                entitySensorBoxZAligned.X2 = entitySensorBoxZAligned.X1;
            }
            else
            {
                entitySensorBoxZAligned.X1 = entitySensorBoxZAligned.X2;
            }

            if (TryStepSmooth(controls, pos, testMotion.Set(0, walkVec.Z), dtFac, FindSteppableCollisionboxSmooth(entityCollisionBox, entitySensorBoxZAligned, moveDelta.Y, testVec.Set(0, walkVec.Y, walkVec.Z)), entityCollisionBox)) return true;
        }

        return false;
    }

    public bool TryStep(EntityPos pos, Vec3d moveDelta, float dtFac, Cuboidd steppableBox, Cuboidd entityCollisionBox)
    {
        if (steppableBox == null) return false;

        double heightDiff = steppableBox.Y2 - entityCollisionBox.Y1 + (0.01 * 3f);
        Vec3d stepPos = newPos.OffsetCopy(moveDelta.X, heightDiff, moveDelta.Z);
        bool canStep = !collisionTester.IsColliding(entity.World.BlockAccessor, entity.CollisionBox, stepPos, false);

        if (canStep)
        {
            pos.Y += 0.07 * dtFac;
            collisionTester.ApplyTerrainCollision(entity, pos, dtFac, ref newPos);
            return true;
        }

        return false;
    }

    public bool TryStepSmooth(EntityControls controls, EntityPos pos, Vec2d walkVec, float dtFac, List<Cuboidd> steppableBoxes, Cuboidd entityCollisionBox)
    {
        if (steppableBoxes == null || steppableBoxes.Count == 0) return false;
        double gravityOffset = 0.03;

        Vec2d walkVecOrtho = new Vec2d(walkVec.Y, -walkVec.X).Normalize();

        double maxX = Math.Abs(walkVecOrtho.X * 0.3) + 0.001;
        double minX = -maxX;
        double maxZ = Math.Abs(walkVecOrtho.Y * 0.3) + 0.001;
        double minZ = -maxZ;
        Cuboidf col = new((float)minX, entity.CollisionBox.Y1, (float)minZ, (float)maxX, entity.CollisionBox.Y2, (float)maxZ);

        double newYPos = pos.Y;
        bool foundStep = false;
        foreach (Cuboidd steppableBox in steppableBoxes)
        {
            double heightDiff = steppableBox.Y2 - entityCollisionBox.Y1 + gravityOffset;
            Vec3d stepPos = new(GameMath.Clamp(newPos.X, steppableBox.MinX, steppableBox.MaxX), newPos.Y + heightDiff, GameMath.Clamp(newPos.Z, steppableBox.MinZ, steppableBox.MaxZ));

            bool canStep = !collisionTester.IsColliding(entity.World.BlockAccessor, col, stepPos, false);

            if (canStep)
            {
                double elevateFactor = controls.Sprint ? 0.10 : controls.Sneak ? 0.025 : 0.05;
                if (!steppableBox.IntersectsOrTouches(entityCollisionBox))
                {
                    newYPos = Math.Max(newYPos, Math.Min(pos.Y + (elevateFactor * dtFac), steppableBox.Y2 - entity.CollisionBox.Y1 + gravityOffset));
                }
                else
                {
                    newYPos = Math.Max(newYPos, pos.Y + (elevateFactor * dtFac));
                }
                foundStep = true;
            }
        }
        if (foundStep)
        {
            pos.Y = newYPos;
            collisionTester.ApplyTerrainCollision(entity, pos, dtFac, ref newPos);
        }
        return foundStep;
    }

    public Cuboidd FindSteppableCollisionBox(Cuboidd entityCollisionBox, double motionY, Vec3d walkVector)
    {
        Cuboidd steppableBox = null;

        int maxCount = collisionTester.CollisionBoxList.Count;
        for (int i = 0; i < maxCount; i++)
        {
            Block block = collisionTester.CollisionBoxList.blocks[i];

            if (!block.CanStep)
            {
                if (entity.CollisionBox.Height < 5 * block.CollisionBoxes[0].Height) continue;
            }

            Cuboidd collisionBox = collisionTester.CollisionBoxList.cuboids[i];
            EnumIntersect intersect = EntityCollisionTester.AabbIntersect(collisionBox, entityCollisionBox, walkVector);
            if (intersect == EnumIntersect.NoIntersect) continue;

            if ((intersect == EnumIntersect.Stuck && !block.AllowStepWhenStuck) || (intersect == EnumIntersect.IntersectY && motionY > 0))
            {
                return null;
            }

            double heightDiff = collisionBox.Y2 - entityCollisionBox.Y1;

            if (heightDiff <= 0) continue;
            if (heightDiff <= stepHeight && (steppableBox == null || steppableBox.Y2 < collisionBox.Y2))
            {
                steppableBox = collisionBox;
            }
        }

        return steppableBox;
    }

    public List<Cuboidd> FindSteppableCollisionboxSmooth(Cuboidd entityCollisionBox, Cuboidd entitySensorBox, double motionY, Vec3d walkVector)
    {
        List<Cuboidd> steppableBoxes = new();
        GetCollidingCollisionBox(entity.World.BlockAccessor, entitySensorBox.ToFloat(), new Vec3d(), out var blocks, true);

        for (int i = 0; i < blocks.Count; i++)
        {
            Cuboidd collisionbox = blocks.cuboids[i];
            Block block = blocks.blocks[i];

            if (!block.CanStep)
            {
                if (entity.CollisionBox.Height < 5 * block.CollisionBoxes[0].Height) continue;
            }

            EnumIntersect intersect = EntityCollisionTester.AabbIntersect(collisionbox, entityCollisionBox, walkVector);

            if ((intersect == EnumIntersect.Stuck && !block.AllowStepWhenStuck) || (intersect == EnumIntersect.IntersectY && motionY > 0))
            {
                return null;
            }

            double heightDiff = collisionbox.Y2 - entityCollisionBox.Y1;

            if (heightDiff <= (entity.CollidedVertically ? 0 : -0.05)) continue;
            if (heightDiff <= stepHeight)
            {
                steppableBoxes.Add(collisionbox);
            }
        }

        return steppableBoxes;
    }

    public static bool GetCollidingCollisionBox(IBlockAccessor blockAccessor, Cuboidf entityBoxRel, Vec3d pos, out CachedCuboidList blocks, bool alsoCheckTouch = true)
    {
        blocks = new CachedCuboidList();
        BlockPos blockPos = new();
        Vec3d blockPosVec = new();
        Cuboidd entityBox = entityBoxRel.ToDouble().Translate(pos);

        int minX = (int)(entityBoxRel.MinX + pos.X);
        int minY = (int)(entityBoxRel.MinY + pos.Y - 1); // -1 for the extra high collision box of fences.
        int minZ = (int)(entityBoxRel.MinZ + pos.Z);
        int maxX = (int)Math.Ceiling(entityBoxRel.MaxX + pos.X);
        int maxY = (int)Math.Ceiling(entityBoxRel.MaxY + pos.Y);
        int maxZ = (int)Math.Ceiling(entityBoxRel.MaxZ + pos.Z);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Block block = blockAccessor.GetBlock(x, y, z);
                    blockPos.Set(x, y, z);
                    blockPosVec.Set(x, y, z);

                    Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, blockPos);

                    for (int i = 0; collisionBoxes != null && i < collisionBoxes.Length; i++)
                    {
                        Cuboidf collBox = collisionBoxes[i];
                        if (collBox == null) continue;

                        bool colliding = alsoCheckTouch ? entityBox.IntersectsOrTouches(collBox, blockPosVec) : entityBox.Intersects(collBox, blockPosVec);

                        if (colliding) blocks.Add(collBox, x, y, z, block);
                    }
                }
            }
        }

        return blocks.Count > 0;
    }

    public override string PropertyName()
    {
        return "entitycontrolledphysics";
    }

    public ref int FlagTickDone { get => ref serverPhysicsTickDone; }
    public volatile int serverPhysicsTickDone;
    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (sapi != null) NIM.RemovePhysicsTickable(entity.Api, this);
    }

    /// <summary>
    /// For adjusting hitbox to dying enemies.
    /// </summary>
    public Matrixf tmpModelMat = new();
    public void AdjustCollisionBoxToAnimation(float dtFac)
    {
        AttachmentPointAndPose apap = entity.AnimManager.Animator?.GetAttachmentPointPose("Center");

        if (apap == null) return;

        float[] hitboxOff = new float[4] { 0, 0, 0, 1 };
        AttachmentPoint ap = apap.AttachPoint;

        float rotX = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateX : 0;
        float rotY = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateY : 0;
        float rotZ = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateZ : 0;

        float[] ModelMat = Mat4f.Create();
        Mat4f.Identity(ModelMat);
        Mat4f.Translate(ModelMat, ModelMat, 0, entity.CollisionBox.Y2 / 2, 0);

        double[] quat = Quaterniond.Create();
        Quaterniond.RotateX(quat, quat, entity.SidedPos.Pitch + (rotX * GameMath.DEG2RAD));
        Quaterniond.RotateY(quat, quat, entity.SidedPos.Yaw + ((rotY + 90) * GameMath.DEG2RAD));
        Quaterniond.RotateZ(quat, quat, entity.SidedPos.Roll + (rotZ * GameMath.DEG2RAD));

        float[] qf = new float[quat.Length];
        for (int k = 0; k < quat.Length; k++) qf[k] = (float)quat[k];
        Mat4f.Mul(ModelMat, ModelMat, Mat4f.FromQuat(Mat4f.Create(), qf));

        float scale = entity.Properties.Client.Size;

        Mat4f.Translate(ModelMat, ModelMat, 0, -entity.CollisionBox.Y2 / 2, 0f);
        Mat4f.Scale(ModelMat, ModelMat, new float[] { scale, scale, scale });
        Mat4f.Translate(ModelMat, ModelMat, -0.5f, 0, -0.5f);

        tmpModelMat.Set(ModelMat)
                    .Mul(apap.AnimModelMatrix)
                    .Translate(ap.PosX / 16f, ap.PosY / 16f, ap.PosZ / 16f);

        EntityPos entityPos = entity.SidedPos;

        float[] endVec = Mat4f.MulWithVec4(tmpModelMat.Values, hitboxOff);

        float motionX = endVec[0] - (entity.CollisionBox.X1 - entity.OriginCollisionBox.X1);
        float motionZ = endVec[2] - (entity.CollisionBox.Z1 - entity.OriginCollisionBox.Z1);

        if (Math.Abs(motionX) > 0.00001 || Math.Abs(motionZ) > 0.00001)
        {
            EntityPos posMoved = entityPos.Copy();
            posMoved.Motion.X = motionX;
            posMoved.Motion.Z = motionZ;

            moveDelta.Set(posMoved.Motion.X, posMoved.Motion.Y, posMoved.Motion.Z);

            collisionTester.ApplyTerrainCollision(entity, posMoved, dtFac, ref newPos);

            double reflectX = ((newPos.X - entityPos.X) / dtFac) - motionX;
            double reflectZ = ((newPos.Z - entityPos.Z) / dtFac) - motionZ;

            entityPos.Motion.X = reflectX;
            entityPos.Motion.Z = reflectZ;

            entity.CollisionBox.Set(entity.OriginCollisionBox);
            entity.CollisionBox.Translate(endVec[0], 0, endVec[2]);

            entity.SelectionBox.Set(entity.OriginSelectionBox);
            entity.SelectionBox.Translate(endVec[0], 0, endVec[2]);
        }
    }
}