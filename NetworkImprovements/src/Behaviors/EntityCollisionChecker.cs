using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

/// <summary>
/// Client only needs to check collision for some thing now.
/// </summary>
public class EntityCollisionChecker : EntityBehavior, PhysicsTickable
{
    [ThreadStatic]
    public static CachingCollisionTester collisionTester;

    public EntityCollisionChecker(Entity entity) : base(entity)
    {
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        NIM.AddPhysicsTickable(entity.Api, this);
    }

    public void OnPhysicsTick(float dt)
    {
        if (entity.State != EnumEntityState.Active) return;

        //Move entity down 0.1, if there's collision set collided
        //Else not collided
        collisionTester ??= new CachingCollisionTester();
        collisionTester.NewTick();

        if (collisionTester.IsColliding(entity.World.BlockAccessor, entity.CollisionBox, entity.ServerPos.XYZ))
        {
            entity.CollidedVertically = true;
        }
        else
        {
            entity.CollidedVertically = false;
        }
    }

    volatile int serverPhysicsTickDone = 0;
    public ref int FlagTickDone { get => ref serverPhysicsTickDone; }

    public void AfterPhysicsTick(float dt)
    {

    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        NIM.RemovePhysicsTickable(entity.Api, this);
    }

    public override string PropertyName()
    {
        return "entitycollisionchecker";
    }
}