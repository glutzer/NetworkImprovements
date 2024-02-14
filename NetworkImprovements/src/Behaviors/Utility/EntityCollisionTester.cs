using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

public class EntityCollisionTester
{
    public CachedCuboidList CollisionBoxList = new();

    public Cuboidd entityBox = new();

    public BlockPos tmpPos = new();
    public Vec3d tmpPosDelta = new();

    public BlockPos minPos = new();
    public BlockPos maxPos = new();

    public EntityCollisionTester()
    {

    }

    public Vec3d pos = new();

    // Sets entity collision flags and calls block collide events.
    public void ApplyTerrainCollision(Entity entity, EntityPos entityPos, float dtFactor, ref Vec3d newPosition, float stepHeight = 1, float yExtra = 1)
    {
        minPos.Set(int.MinValue, int.MinValue, int.MinValue);

        IWorldAccessor worldAccessor = entity.World;

        pos.X = entityPos.X;
        pos.Y = entityPos.Y;
        pos.Z = entityPos.Z;

        EnumPushDirection pushDirection = EnumPushDirection.None;

        entityBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);
        entityBox.RemoveRoundingErrors();

        double deltaX = entityPos.Motion.X * dtFactor;
        double deltaY = entityPos.Motion.Y * dtFactor;
        double deltaZ = entityPos.Motion.Z * dtFactor;

        // Generate a cube that encompasses every block between the old and new position.
        // This could also just take the new position and old position without using motion.
        GenerateCollisionBoxList(worldAccessor.BlockAccessor, deltaX, deltaY, deltaZ, stepHeight, yExtra);

        bool collided = false;

        int collisionBoxListCount = CollisionBoxList.Count;

        // ---------- Y COLLISION. Call events and set collided vertically.
        for (int i = 0; i < collisionBoxListCount; i++)
        {
            deltaY = CollisionBoxList.cuboids[i].pushOutY(entityBox, deltaY, ref pushDirection);
            if (pushDirection == EnumPushDirection.None) continue;

            collided = true;

            CollisionBoxList.blocks[i].OnEntityCollide(
                worldAccessor,
                entity,
                CollisionBoxList.positions[i],
                pushDirection == EnumPushDirection.Negative ? BlockFacing.UP : BlockFacing.DOWN,
                tmpPosDelta.Set(deltaX, deltaY, deltaZ),
                !entity.CollidedVertically
            );
        }
        entityBox.Translate(0, deltaY, 0);

        entity.CollidedVertically = collided;

        // Check if horizontal collision is possible.
        bool horizontallyBlocked = false;
        entityBox.Translate(deltaX, 0, deltaZ);
        foreach (Cuboidd cuboid in CollisionBoxList)
        {
            if (cuboid.Intersects(entityBox))
            {
                horizontallyBlocked = true;
                break;
            }
        }
        entityBox.Translate(-deltaX, 0, -deltaZ);

        collided = false;

        if (horizontallyBlocked)
        {
            // ---------- X COLLISION. Call events and set collided horizontally.
            for (int i = 0; i < collisionBoxListCount; i++)
            {
                deltaX = CollisionBoxList.cuboids[i].pushOutX(entityBox, deltaX, ref pushDirection);
                if (pushDirection == EnumPushDirection.None) continue;

                collided = true;

                CollisionBoxList.blocks[i].OnEntityCollide(
                    worldAccessor,
                    entity,
                    CollisionBoxList.positions[i],
                    pushDirection == EnumPushDirection.Negative ? BlockFacing.EAST : BlockFacing.WEST,
                    tmpPosDelta.Set(deltaX, deltaY, deltaZ),
                    !entity.CollidedHorizontally
                );
            }
            entityBox.Translate(deltaX, 0, 0);

            // ---------- Z COLLISION. Call events and set collided horizontally.
            for (int i = 0; i < collisionBoxListCount; i++)
            {
                deltaZ = CollisionBoxList.cuboids[i].pushOutZ(entityBox, deltaZ, ref pushDirection);
                if (pushDirection == EnumPushDirection.None) continue;

                collided = true;

                CollisionBoxList.blocks[i].OnEntityCollide(
                    worldAccessor,
                    entity,
                    CollisionBoxList.positions[i],
                    pushDirection == EnumPushDirection.Negative ? BlockFacing.SOUTH : BlockFacing.NORTH,
                    tmpPosDelta.Set(deltaX, deltaY, deltaZ),
                    !entity.CollidedHorizontally
                );
            }
        }

        entity.CollidedHorizontally = collided;

        if (deltaY > 0 && entity.CollidedVertically) deltaY -= entity.LadderFixDelta;

        newPosition.Set(pos.X + deltaX, pos.Y + deltaY, pos.Z + deltaZ);
    }

    // Does not call block collide events or set collided flags. Used for peeking position in the future.
    public void PeekCollision(Entity entity, EntityPos entityPos, float dtFactor, ref Vec3d newPosition)
    {
        minPos.Set(int.MinValue, int.MinValue, int.MinValue);

        pos.X = entityPos.X;
        pos.Y = entityPos.Y;
        pos.Z = entityPos.Z;

        EnumPushDirection pushDirection = EnumPushDirection.None;

        entityBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);
        entityBox.RemoveRoundingErrors();

        double deltaX = entityPos.Motion.X * dtFactor;
        double deltaY = entityPos.Motion.Y * dtFactor;
        double deltaZ = entityPos.Motion.Z * dtFactor;

        int collisionBoxListCount = CollisionBoxList.Count;

        // ---------- Y COLLISION. Call events and set collided vertically.
        for (int i = 0; i < collisionBoxListCount; i++) deltaY = CollisionBoxList.cuboids[i].pushOutY(entityBox, deltaY, ref pushDirection);
        entityBox.Translate(0, deltaY, 0);

        // Check if horizontal collision is possible.
        bool horizontallyBlocked = false;
        entityBox.Translate(deltaX, 0, deltaZ);
        foreach (Cuboidd cuboid in CollisionBoxList)
        {
            if (cuboid.Intersects(entityBox))
            {
                horizontallyBlocked = true;
                break;
            }
        }
        entityBox.Translate(-deltaX, 0, -deltaZ);

        if (horizontallyBlocked)
        {
            // ---------- X COLLISION. Call events and set collided horizontally.
            for (int i = 0; i < collisionBoxListCount; i++) deltaX = CollisionBoxList.cuboids[i].pushOutX(entityBox, deltaX, ref pushDirection);
            entityBox.Translate(deltaX, 0, 0);

            // ---------- Z COLLISION. Call events and set collided horizontally.
            for (int i = 0; i < collisionBoxListCount; i++) deltaZ = CollisionBoxList.cuboids[i].pushOutZ(entityBox, deltaZ, ref pushDirection);
        }

        if (deltaY > 0 && entity.CollidedVertically) deltaY -= entity.LadderFixDelta;

        newPosition.Set(pos.X + deltaX, pos.Y + deltaY, pos.Z + deltaZ);
    }

    public void GenerateCollisionBoxList(IBlockAccessor blockAccessor, double deltaX, double deltaY, double deltaZ, float stepHeight, float yExtra)
    {
        // Check if the min and max positions of the collision test are unchanged and use the old list if they are.
        bool minPosIsUnchanged = minPos.SetAndEquals(
            (int)(entityBox.X1 + Math.Min(0, deltaX)),
            (int)(entityBox.Y1 + Math.Min(0, deltaY) - yExtra), // yExtra looks at blocks below to allow for the extra high collision box of fences.
            (int)(entityBox.Z1 + Math.Min(0, deltaZ))
        );

        double y2 = Math.Max(entityBox.Y1 + stepHeight, entityBox.Y2);

        bool maxPosIsUnchanged = maxPos.SetAndEquals(
            (int)(entityBox.X2 + Math.Max(0, deltaX)),
            (int)(y2 + Math.Max(0, deltaY)),
            (int)(entityBox.Z2 + Math.Max(0, deltaZ))
        );

        if (minPosIsUnchanged && maxPosIsUnchanged) return;

        // Clear the list and add every cuboid the block has to it.
        CollisionBoxList.Clear();
        blockAccessor.WalkBlocks(minPos, maxPos, (block, x, y, z) => {
            Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, tmpPos.Set(x, y, z));
            if (collisionBoxes != null)
            {
                CollisionBoxList.Add(collisionBoxes, x, y, z, block);
            }
        }, true);
    }

    public static bool AabbIntersect(Cuboidf aabb, double x, double y, double z, Cuboidf aabb2, Vec3d pos)
    {
        if (aabb2 == null) return true;

        return
            x + aabb.X1 < aabb2.X2 + pos.X &&
            x + aabb.X2 > aabb2.X1 + pos.X &&
            z + aabb.Z1 < aabb2.Z2 + pos.Z &&
            z + aabb.Z2 > aabb2.Z1 + pos.Z &&
            y + aabb.Y1 < aabb2.Y2 + pos.Y &&
            y + aabb.Y2 > aabb2.Y1 + pos.Y
        ;
    }

    public static EnumIntersect AabbIntersect(Cuboidd aabb, Cuboidd aabb2, Vec3d motion)
    {
        if (aabb.Intersects(aabb2)) return EnumIntersect.Stuck;

        // X
        if (
            aabb.X1 < aabb2.X2 + motion.X &&
            aabb.X2 > aabb2.X1 + motion.X &&
            aabb.Z1 < aabb2.Z2 &&
            aabb.Z2 > aabb2.Z1 &&
            aabb.Y1 < aabb2.Y2 &&
            aabb.Y2 > aabb2.Y1
        ) return EnumIntersect.IntersectX;

        // Y
        if (
            aabb.X1 < aabb2.X2 &&
            aabb.X2 > aabb2.X1 &&
            aabb.Z1 < aabb2.Z2 &&
            aabb.Z2 > aabb2.Z1 &&
            aabb.Y1 < aabb2.Y2 + motion.Y &&
            aabb.Y2 > aabb2.Y1 + motion.Y
        ) return EnumIntersect.IntersectY;

        // Z
        if (
            aabb.X1 < aabb2.X2 &&
            aabb.X2 > aabb2.X1 &&
            aabb.Z1 < aabb2.Z2 + motion.Z &&
            aabb.Z2 > aabb2.Z1 + motion.Z &&
            aabb.Y1 < aabb2.Y2 &&
            aabb.Y2 > aabb2.Y1
        ) return EnumIntersect.IntersectZ;

        return EnumIntersect.NoIntersect;
    }

    readonly Cuboidd tmpBox = new();
    readonly BlockPos blockPos = new();
    readonly Vec3d blockPosVec = new();

    public bool IsColliding(IBlockAccessor blockAccessor, Cuboidf entityBoxRel, Vec3d pos, bool alsoCheckTouch = true)
    {
        return GetCollidingBlock(blockAccessor, entityBoxRel, pos, alsoCheckTouch) != null;
    }

    public Block GetCollidingBlock(IBlockAccessor blockAccessor, Cuboidf entityBoxRel, Vec3d pos, bool alsoCheckTouch = true)
    {
        Cuboidd entityBox = tmpBox.SetAndTranslate(entityBoxRel, pos);

        int minX = (int)entityBox.X1;
        int minY = (int)entityBox.Y1 - 1; // -1 for the extra high collision box of fences.
        int minZ = (int)entityBox.Z1;

        int maxX = (int)entityBox.X2;
        int maxY = (int)entityBox.Y2;
        int maxZ = (int)entityBox.Z2;

        entityBox.Y1 = Math.Round(entityBox.Y1, 5); // Fix float/double rounding errors. Only need to fix the vertical because gravity.

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Block block = blockAccessor.GetBlock(x, y, z, BlockLayersAccess.MostSolid);
                    blockPos.Set(x, y, z);

                    Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, blockPos);
                    if (collisionBoxes == null || collisionBoxes.Length == 0) continue;

                    blockPosVec.Set(x, y, z);
                    for (int i = 0; i < collisionBoxes.Length; i++)
                    {
                        Cuboidf collBox = collisionBoxes[i];
                        if (collBox == null) continue;

                        if (alsoCheckTouch ? entityBox.IntersectsOrTouches(collBox, blockPosVec) : entityBox.Intersects(collBox, blockPosVec)) return block;
                    }
                }
            }
        }

        return null;
    }

    // Returns the cuboid the entity is colliding with.
    public static Cuboidd GetCollidingCollisionBox(IBlockAccessor blockAccessor, Cuboidf entityBoxRel, Vec3d pos, bool alsoCheckTouch = true)
    {
        BlockPos blockPos = new();
        Vec3d blockPosVec = new();
        Cuboidd entityBox = entityBoxRel.ToDouble().Translate(pos);

        entityBox.Y1 = Math.Round(entityBox.Y1, 5); // Fix float/double rounding errors. Only need to fix the vertical because gravity.

        int minX = (int)(entityBoxRel.X1 + pos.X);
        int minY = (int)(entityBoxRel.Y1 + pos.Y - 1); // -1 for the extra high collision box of fences
        int minZ = (int)(entityBoxRel.Z1 + pos.Z);

        int maxX = (int)Math.Ceiling(entityBoxRel.X2 + pos.X);
        int maxY = (int)Math.Ceiling(entityBoxRel.Y2 + pos.Y);
        int maxZ = (int)Math.Ceiling(entityBoxRel.Z2 + pos.Z);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Block block = blockAccessor.GetMostSolidBlock(x, y, z);
                    blockPos.Set(x, y, z);
                    blockPosVec.Set(x, y, z);

                    Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, blockPos);

                    for (int i = 0; collisionBoxes != null && i < collisionBoxes.Length; i++)
                    {
                        Cuboidf collBox = collisionBoxes[i];
                        if (collBox == null) continue;

                        bool colliding = alsoCheckTouch ? entityBox.IntersectsOrTouches(collBox, blockPosVec) : entityBox.Intersects(collBox, blockPosVec);
                        if (colliding)
                        {
                            return collBox.ToDouble().Translate(blockPos);
                        }
                    }
                }
            }
        }

        return null;
    }

    // Returns if the entity is colliding with terrain and returns the box it's colliding with.
    public static bool GetCollidingCollisionBox(IBlockAccessor blockAccessor, Cuboidf entityBoxRel, Vec3d pos, ref Cuboidd intoCuboid, bool alsoCheckTouch = true)
    {
        BlockPos blockPos = new();
        Vec3d blockPosVec = new();
        Cuboidd entityBox = entityBoxRel.ToDouble().Translate(pos);

        entityBox.Y1 = Math.Round(entityBox.Y1, 5); // Fix float/double rounding errors. Only need to fix the vertical because gravity.

        int minX = (int)(entityBoxRel.X1 + pos.X);
        int minY = (int)(entityBoxRel.Y1 + pos.Y - 1); // -1 for the extra high collision box of fences.
        int minZ = (int)(entityBoxRel.Z1 + pos.Z);

        int maxX = (int)Math.Ceiling(entityBoxRel.X2 + pos.X);
        int maxY = (int)Math.Ceiling(entityBoxRel.Y2 + pos.Y);
        int maxZ = (int)Math.Ceiling(entityBoxRel.Z2 + pos.Z);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Block block = blockAccessor.GetBlock(x, y, z, BlockLayersAccess.MostSolid);
                    blockPos.Set(x, y, z);
                    blockPosVec.Set(x, y, z);

                    Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, blockPos);

                    for (int i = 0; collisionBoxes != null && i < collisionBoxes.Length; i++)
                    {
                        Cuboidf collBox = collisionBoxes[i];
                        if (collBox == null) continue;

                        bool colliding = alsoCheckTouch ? entityBox.IntersectsOrTouches(collBox, blockPosVec) : entityBox.Intersects(collBox, blockPosVec);
                        if (colliding)
                        {
                            intoCuboid.Set(collBox).Translate(blockPos);
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
}