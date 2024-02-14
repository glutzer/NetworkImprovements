using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

public abstract class PhysicsBehaviorBase : EntityBehavior
{
    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    // How often the client should be sending updates.
    public float clientInterval = 1 / 15f;

    public int previousVersion;

    public bool isMountable;

    public EntityPos lPos = new();
    public Vec3d nPos;

    public PhysicsBehaviorBase(Entity entity) : base(entity)
    {
    }

    public EntityCollisionTester collisionTester = new();
    public float collisionYExtra = 1f;

    public void Init()
    {
        if (entity.Api is ICoreClientAPI capi) this.capi = capi;
        if (entity.Api is ICoreServerAPI sapi) this.sapi = sapi;
        isMountable = entity is IMountable || entity is IMountableSupplier;
    }

    public override string PropertyName()
    {
        return "";
    }
}