using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

/// <summary>
/// New version of controlled physics only applied on the server.
/// Applies 30 times per second (or same as physics manager).
/// </summary>
public class EntityControlledPhysics : EntityBehavior, PhysicsTickable
{
    public const double collisionboxReductionForInsideBlocksCheck = 0.009;

    public ICoreAPI api;

    [ThreadStatic]
    protected static CachingCollisionTester collisionTester;

    public float stepHeight = 0.6f; //Read by AITaskTargetableBase
    public bool smoothStepping = false;
    public bool isMountable;

    public List<PModule> physicsModules = new();

    public int tickCounter = 0;

    public Vec3d prevPos = new();
    public Vec3d nextPosition = new();
    public Vec3d moveDelta = new();
    public BlockPos tmpPos = new(0);
    public Cuboidd entityBox = new();
    public Vec3d outPos = new();

    public List<FastVec3i> traversed = new(4);
    public IComparer<FastVec3i> fastVec3iComparer = new FastVec3iComparer();

    public EntityControlledPhysics(Entity entity) : base(entity)
    {
        api = entity.Api;
        isMountable = entity is IMountable || entity is IMountableSupplier;
    }

    /// <summary>
    /// What modules will be applied in order to this entity.
    /// </summary>
    public virtual void SetModules()
    {
        physicsModules.Add(new PModuleOnGround());
        physicsModules.Add(new PModuleInLiquid());
        physicsModules.Add(new PModuleInAir());
        physicsModules.Add(new PModuleGravity());
        physicsModules.Add(new PModuleMotionDrag());
    }

    /// <summary>
    /// Called when entity is loaded.
    /// </summary>
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

        if (entity is not EntityPlayer)
        {
            NIM.AddPhysicsTickable(api, this);
        }
    }

    /// <summary>
    /// Called 30 times per second on both client and server.
    /// </summary>
    public virtual void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        //Clear all traversed blocks this tick
        traversed.Clear();

        SetupKnockbackValues(); //REPLACE KNOCKBACK WITH A JUMP-LIKE MOTION IN PHYSICS MODULES

        //Setup collision for a new tick
        collisionTester ??= new CachingCollisionTester();
        collisionTester.NewTick();

        prevPos.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);

        //Get controls
        EntityControls controls = ((EntityAgent)entity).Controls;

        //Apply to server position only
        EntityPos pos = entity.ServerPos;

        //Shared loop
        TickEntityPhysics(pos, controls, dt);

        //Using dt instead of accum
        entity.PhysicsUpdateWatcher?.Invoke(dt, prevPos);
    }

    /// <summary>
    /// Shared between classes.
    /// </summary>
    public void TickEntityPhysics(EntityPos pos, EntityControls controls, float dt)
    {
        float dtFactor = dt * 60;

        //Adjust collision box of dead entities
        if (!entity.Alive) AdjustCollisionBoxToAnimation(dtFactor);

        //Apply every physics module
        foreach (PModule physicsModule in physicsModules)
        {
            if (physicsModule.Applicable(entity, pos, controls))
            {
                physicsModule.DoApply(dt, entity, pos, controls);
            }
        }

        EntityAgent agent = entity as EntityAgent;
        if (agent?.MountedOn != null)
        {
            entity.Swimming = false;
            entity.OnGround = false;

            //Originally only set on the client but it should be set here on the server
            //If the entities are at the same position after physics ticks they should interpolate smoothly
            pos.SetPos(agent.MountedOn.MountPosition);

            pos.Motion.X = 0;
            pos.Motion.Y = 0;
            pos.Motion.Z = 0;
            return;
        }

        //Clamp motion over 100 blocks/s
        if (pos.Motion.LengthSq() > 100)
        {
            pos.Motion.X = GameMath.Clamp(pos.Motion.X, -10, 10);
            pos.Motion.Y = GameMath.Clamp(pos.Motion.Y, -10, 10);
            pos.Motion.Z = GameMath.Clamp(pos.Motion.Z, -10, 10);
        }

        //Collide with blocks if noclip isn't on
        if (!controls.NoClip)
        {
            //This handles pretty much everything else like increasing position by motion
            CollideAndMove(pos, controls, dt, dtFactor);
        }
        else
        {
            //Apply extra motion while noclipping?
            pos.X += pos.Motion.X * dt * 60f;
            pos.Y += pos.Motion.Y * dt * 60f;
            pos.Z += pos.Motion.Z * dt * 60f;

            entity.Swimming = false;
            entity.FeetInLiquid = false;
            entity.OnGround = false;
        }
    }

    /// <summary>
    /// Call on entity inside events.
    /// </summary>
    public virtual void AfterPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        //Call OnEntityInside events
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

    public override string PropertyName()
    {
        return "entitycontrolledphysics";
    }

    public ref int FlagTickDone { get => ref serverPhysicsTickDone; }
    public volatile int serverPhysicsTickDone;
    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        NIM.RemovePhysicsTickable(api, this);
    }

    #region Knockback

    public Vec3d knockbackDirection;
    public int knockbackState;
    public void SetupKnockbackValues()
    {
        //No accumulation for now. See what happens with these values
        //dmgkb is set back to 0 in controlled physics somewhere
        knockbackState = entity.Attributes.GetInt("dmgkb");
        if (knockbackState > 0)
        {
            if (knockbackState == 1)
            {
                double kbX = entity.WatchedAttributes.GetDouble("kbdirX");
                double kbY = entity.WatchedAttributes.GetDouble("kbdirY");
                double kbZ = entity.WatchedAttributes.GetDouble("kbdirZ");
                knockbackDirection = new Vec3d(kbX, kbY, kbZ);
            }
        }
        else
        {
            knockbackDirection = null;
        }
    }

    #endregion

    #region Collision

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
        Quaterniond.RotateX(quat, quat, entity.Pos.Pitch + rotX * GameMath.DEG2RAD);
        Quaterniond.RotateY(quat, quat, entity.Pos.Yaw + (rotY + 90) * GameMath.DEG2RAD);
        Quaterniond.RotateZ(quat, quat, entity.Pos.Roll + rotZ * GameMath.DEG2RAD);

        float[] qf = new float[quat.Length];
        for (int k = 0; k < quat.Length; k++) qf[k] = (float)quat[k];
        Mat4f.Mul(ModelMat, ModelMat, Mat4f.FromQuat(Mat4f.Create(), qf));

        float scale = entity.Properties.Client.Size;

        Mat4f.Translate(ModelMat, ModelMat, 0, -entity.CollisionBox.Y2 / 2, 0f);
        Mat4f.Scale(ModelMat, ModelMat, new float[] { scale, scale, scale });
        Mat4f.Translate(ModelMat, ModelMat, -0.5f, 0, -0.5f);

        tmpModelMat
            .Set(ModelMat)
            .Mul(apap.AnimModelMatrix)
            .Translate(ap.PosX / 16f, ap.PosY / 16f, ap.PosZ / 16f)
        ;

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

            collisionTester.ApplyTerrainCollision(entity, posMoved, dtFac, ref outPos);

            double reflectX = (outPos.X - entityPos.X) / dtFac - motionX;
            double reflectZ = (outPos.Z - entityPos.Z) / dtFac - motionZ;

            entityPos.Motion.X = reflectX;
            entityPos.Motion.Z = reflectZ;

            entity.CollisionBox.Set(entity.OriginCollisionBox);
            entity.CollisionBox.Translate(endVec[0], 0, endVec[2]);

            entity.SelectionBox.Set(entity.OriginSelectionBox);
            entity.SelectionBox.Translate(endVec[0], 0, endVec[2]);
        }
    }

    /// <summary>
    /// Do all collisions with blocks.
    /// Handle the rest.
    /// </summary>
    public void CollideAndMove(EntityPos pos, EntityControls controls, float dt, float dtFactor)
    {
        IBlockAccessor blockAccess = entity.World.BlockAccessor;
        double prevYMotion = pos.Motion.Y;

        //Set amount that should be moved this physics tick based on the motion
        moveDelta.Set(pos.Motion.X * dtFactor, prevYMotion * dtFactor, pos.Motion.Z * dtFactor);

        //Set next position the entity will be at after this
        nextPosition.Set(pos.X + moveDelta.X, pos.Y + moveDelta.Y, pos.Z + moveDelta.Z);

        //If y is negative you're falling
        bool falling = prevYMotion < 0;

        //Variables related to previous position
        bool feetInLiquidBefore = entity.FeetInLiquid;
        bool onGroundBefore = entity.OnGround;
        bool swimmingBefore = entity.Swimming;

        //Re-check climbing
        controls.IsClimbing = false;
        entity.ClimbingOnFace = null;
        entity.ClimbingIntoFace = null;

        //Apply climbing controls, all adjusts motion
        if (entity.Properties.CanClimb == true)
        {
            int height = (int)Math.Ceiling(entity.CollisionBox.Y2);

            entityBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);

            for (int dy = 0; dy < height; dy++)
            {
                tmpPos.Set((int)pos.X, (int)pos.Y + dy, (int)pos.Z);
                Block inBlock = blockAccess.GetBlock(tmpPos);
                if (!inBlock.IsClimbable(tmpPos) && !entity.Properties.CanClimbAnywhere) continue;

                Cuboidf[] collBoxes = inBlock.GetCollisionBoxes(blockAccess, tmpPos);
                if (collBoxes == null) continue;

                for (int i = 0; i < collBoxes.Length; i++)
                {
                    double dist = entityBox.ShortestDistanceFrom(collBoxes[i], tmpPos);
                    controls.IsClimbing |= dist < entity.Properties.ClimbTouchDistance;

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
                    Block inBlock = blockAccess.GetBlock(tmpPos);

                    Cuboidf[] collBoxes = inBlock.GetCollisionBoxes(blockAccess, tmpPos);
                    entity.ClimbingIntoFace = (collBoxes != null && collBoxes.Length != 0) ? walkIntoFace : null;
                }
            }

            for (int i = 0; !controls.IsClimbing && i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing facing = BlockFacing.HORIZONTALS[i];
                for (int dy = 0; dy < height; dy++)
                {
                    tmpPos.Set((int)pos.X + facing.Normali.X, (int)pos.Y + dy, (int)pos.Z + facing.Normali.Z);
                    Block inBlock = blockAccess.GetBlock(tmpPos);
                    if (!inBlock.IsClimbable(tmpPos) && !(entity.Properties.CanClimbAnywhere && entity.Alive)) continue;

                    Cuboidf[] collBoxes = inBlock.GetCollisionBoxes(blockAccess, tmpPos);
                    if (collBoxes == null) continue;

                    for (int j = 0; j < collBoxes.Length; j++)
                    {
                        double distance = entityBox.ShortestDistanceFrom(collBoxes[j], tmpPos);
                        controls.IsClimbing |= distance < entity.Properties.ClimbTouchDistance;

                        if (controls.IsClimbing)
                        {
                            entity.ClimbingOnFace = facing;
                            entity.ClimbingOnCollBox = collBoxes[j];
                            break;
                        }
                    }
                }
            }
        }

        if (controls.IsClimbing)
        {
            if (controls.WalkVector.Y == 0)
            {
                pos.Motion.Y = controls.Sneak ? Math.Max(-0.07, pos.Motion.Y - 0.07) : pos.Motion.Y;
                if (controls.Jump) pos.Motion.Y = 0.035 * dt * 60f;
            }
        }

        //Test collision with terrain
        collisionTester.ApplyTerrainCollision(entity, pos, dtFactor, ref outPos, stepHeight);

        //Step up blocks
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

        //Sneaking, whether you will run off a cliff or not
        HandleSneaking(pos, controls, dt);

        if (entity.CollidedHorizontally && !controls.IsClimbing && !controls.IsStepping && entity.Properties.Habitat != EnumHabitat.Underwater)
        {
            if (blockAccess.GetBlock((int)pos.X, (int)(pos.Y + 0.5), (int)pos.Z).LiquidLevel >= 7 || blockAccess.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z).LiquidLevel >= 7 || (blockAccess.GetBlock((int)pos.X, (int)(pos.Y - 0.05), (int)pos.Z).LiquidLevel >= 7))
            {
                pos.Motion.Y += 0.2 * dt;
                controls.IsStepping = true;
            }
            else //Attempt to prevent endless collisions
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

        if (outPos.X != pos.X && blockAccess.IsNotTraversable(pos.X + pos.Motion.X * dt * 60f, pos.Y, pos.Z))
        {
            outPos.X = pos.X;
        }
        if (outPos.Y != pos.Y && blockAccess.IsNotTraversable(pos.X, pos.Y + pos.Motion.Y * dt * 60f, pos.Z))
        {
            outPos.Y = pos.Y;
        }
        if (outPos.Z != pos.Z && blockAccess.IsNotTraversable(pos.X, pos.Y, pos.Z + pos.Motion.Z * dt * 60f))
        {
            outPos.Z = pos.Z;
        }

        pos.SetPos(outPos);

        //Set motion to 0 if collision detected

        if ((nextPosition.X < outPos.X && pos.Motion.X < 0) || (nextPosition.X > outPos.X && pos.Motion.X > 0))
        {
            pos.Motion.X = 0;
        }

        if ((nextPosition.Y < outPos.Y && pos.Motion.Y < 0) || (nextPosition.Y > outPos.Y && pos.Motion.Y > 0))
        {
            pos.Motion.Y = 0;
        }

        if ((nextPosition.Z < outPos.Z && pos.Motion.Z < 0) || (nextPosition.Z > outPos.Z && pos.Motion.Z > 0))
        {
            pos.Motion.Z = 0;
        }

        float offX = entity.CollisionBox.X2 - entity.OriginCollisionBox.X2;
        float offZ = entity.CollisionBox.Z2 - entity.OriginCollisionBox.Z2;

        int posX = (int)(pos.X + offX);
        int posZ = (int)(pos.Z + offZ);

        Block blockFluid = blockAccess.GetBlock(posX, (int)pos.Y, posZ, BlockLayersAccess.Fluid);
        Block middleWOIBlock = blockAccess.GetBlock(posX, (int)(pos.Y + entity.SwimmingOffsetY), posZ, BlockLayersAccess.Fluid);

        entity.OnGround = (entity.CollidedVertically && falling && !controls.IsClimbing) || controls.IsStepping;
        entity.FeetInLiquid = false;
        if (blockFluid.IsLiquid())
        {
            Block aboveBlock = blockAccess.GetBlock(posX, (int)(pos.Y + 1), posZ, BlockLayersAccess.Fluid);
            entity.FeetInLiquid = (blockFluid.LiquidLevel + (aboveBlock.LiquidLevel > 0 ? 1 : 0)) / 8f >= pos.Y - (int)pos.Y;
        }
        entity.InLava = blockFluid.LiquidCode == "lava";
        entity.Swimming = middleWOIBlock.IsLiquid();

        //Entity behavior health calls this
        //With the player the client has authority over this and sends a packet to the server to call this. This way there's no wrong fall damage.
        if (!onGroundBefore && entity.OnGround)
        {
            entity.OnFallToGround(prevYMotion);
        }

        if ((!entity.Swimming && !feetInLiquidBefore && entity.FeetInLiquid) || (!entity.FeetInLiquid && !swimmingBefore && entity.Swimming))
        {
            entity.OnCollideWithLiquid();
        }

        if ((swimmingBefore && !entity.Swimming && !entity.FeetInLiquid) || (feetInLiquidBefore && !entity.FeetInLiquid && !entity.Swimming))
        {
            entity.OnExitedLiquid();
        }

        if (!falling || entity.OnGround || controls.IsClimbing)
        {
            entity.PositionBeforeFalling.Set(outPos);
        }

        //Not sure what this is
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
    }

    #endregion

    #region Sneaking

    //Sneaking entities can't leap over cliffs
    public Cuboidf sneakTestCollisionbox = new();
    public void HandleSneaking(EntityPos pos, EntityControls controls, float dt)
    {
        if (!controls.Sneak || !entity.OnGround || pos.Motion.Y > 0) return;

        //Sneak to prevent falling off blocks
        Vec3d testPosition = new();
        testPosition.Set(pos.X, pos.Y - GlobalConstants.GravityPerSecond * dt, pos.Z);

        //Only apply this if the entity is on the ground in the first place
        if (!collisionTester.IsColliding(entity.World.BlockAccessor, sneakTestCollisionbox, testPosition)) return;

        tmpPos.Set((int)pos.X, (int)pos.Y - 1, (int)pos.Z);
        Block belowBlock = entity.World.BlockAccessor.GetBlock(tmpPos);

        //Test for X
        testPosition.Set(outPos.X, outPos.Y - GlobalConstants.GravityPerSecond * dt, pos.Z);
        if (!collisionTester.IsColliding(entity.World.BlockAccessor, sneakTestCollisionbox, testPosition))
        {
            //COMMENT: weird hack so you can climb down ladders more easily
            if (belowBlock.IsClimbable(tmpPos))
            {
                outPos.X += (pos.X - outPos.X) / 10;
            }
            else
            {
                outPos.X = pos.X;
            }
        }

        //Test for Z
        testPosition.Set(pos.X, outPos.Y - GlobalConstants.GravityPerSecond * dt, outPos.Z);
        if (!collisionTester.IsColliding(entity.World.BlockAccessor, sneakTestCollisionbox, testPosition))
        {
            //COMMENT: weird hack so you can climb down ladders more easily
            if (belowBlock.IsClimbable(tmpPos))
            {
                outPos.Z += (pos.Z - outPos.Z) / 10;
            }
            else
            {
                outPos.Z = pos.Z;
            }
        }
    }

    #endregion

    #region Stepping

    public Cuboidd steppingCollisionBox = new();
    public Vec3d steppingTestVec = new();
    public Vec3d steppingTestMotion = new();
    private bool HandleSteppingOnBlocks(EntityPos pos, Vec3d moveDelta, float dtFac, EntityControls controls)
    {
        if (controls.WalkVector.X == 0 && controls.WalkVector.Z == 0) return false; //Don't attempt to step up if not moving

        if ((!entity.OnGround && !entity.Swimming) || entity.Properties.Habitat == EnumHabitat.Underwater) return false; //Don't try to step up if a fish or if flying

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

    private bool HandleSteppingOnBlocksSmooth(EntityPos pos, Vec3d moveDelta, float dtFac, EntityControls controls)
    {
        if (!controls.TriesToMove || (!entity.OnGround && !entity.Swimming) || entity.Properties.Habitat == EnumHabitat.Underwater) return false;

        Cuboidd entityCollisionBox = entity.CollisionBox.ToDouble();

        //COMMENT:
        //How far ahead to scan for steppable blocks 
        //TODO needs to be increased for large and fast creatures (wolves)
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

        double heightDiff = steppableBox.Y2 - entityCollisionBox.Y1 + 0.01 * 3f; //COMMENT: This added constant value is an ugly hack because outposition has gravity added, but has not adjusted its position to the ground floor yet
        Vec3d stepPos = outPos.OffsetCopy(moveDelta.X, heightDiff, moveDelta.Z);
        bool canStep = !collisionTester.IsColliding(entity.World.BlockAccessor, entity.CollisionBox, stepPos, false);

        if (canStep)
        {
            pos.Y += 0.07 * dtFac;
            collisionTester.ApplyTerrainCollision(entity, pos, dtFac, ref outPos);
            return true;
        }

        return false;
    }

    public bool TryStepSmooth(EntityControls controls, EntityPos pos, Vec2d walkVec, float dtFac, List<Cuboidd> steppableBoxes, Cuboidd entityCollisionBox)
    {
        if (steppableBoxes == null || steppableBoxes.Count == 0) return false;
        double gravityOffset = 0.03; //COMMENT: This added constant value is an ugly hack because outposition has gravity added, but has not adjusted its position to the ground floor yet

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
            Vec3d stepPos = new(GameMath.Clamp(outPos.X, steppableBox.MinX, steppableBox.MaxX), outPos.Y + heightDiff, GameMath.Clamp(outPos.Z, steppableBox.MinZ, steppableBox.MaxZ));

            bool canStep = !collisionTester.IsColliding(entity.World.BlockAccessor, col, stepPos, false);

            if (canStep)
            {
                double elevateFactor = controls.Sprint ? 0.10 : controls.Sneak ? 0.025 : 0.05;
                if (!steppableBox.IntersectsOrTouches(entityCollisionBox))
                {
                    newYPos = Math.Max(newYPos, Math.Min(pos.Y + elevateFactor * dtFac, steppableBox.Y2 - entity.CollisionBox.Y1 + gravityOffset));
                }
                else
                {
                    newYPos = Math.Max(newYPos, pos.Y + elevateFactor * dtFac);
                }
                foundStep = true;
            }
        }
        if (foundStep)
        {
            pos.Y = newYPos;
            collisionTester.ApplyTerrainCollision(entity, pos, dtFac, ref outPos);
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
                //COMMENT: Blocks which are low relative to this entity (e.g. small troughs are low for the player) can still be stepped on
                if (entity.CollisionBox.Height < 5 * block.CollisionBoxes[0].Height) continue;
            }

            Cuboidd collisionBox = collisionTester.CollisionBoxList.cuboids[i];
            EnumIntersect intersect = CollisionTester.AabbIntersect(collisionBox, entityCollisionBox, walkVector);
            if (intersect == EnumIntersect.NoIntersect) continue;

            //COMMENT:
            //Already stuck somewhere? Can't step stairs
            //Would get stuck vertically if I go up? Can't step up either
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
                //COMMENT: Blocks which are low relative to this entity (e.g. small troughs are low for the player) can still be stepped on
                if (entity.CollisionBox.Height < 5 * block.CollisionBoxes[0].Height) continue;
            }

            EnumIntersect intersect = CollisionTester.AabbIntersect(collisionbox, entityCollisionBox, walkVector);

            //COMMENT:
            //Already stuck somewhere? Can't step stairs
            //Would get stuck vertically if I go up? Can't step up either
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
        int minY = (int)(entityBoxRel.MinY + pos.Y - 1); //-1 for the extra high collision box of fences
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

    #endregion
}