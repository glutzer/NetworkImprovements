using ProtoBuf;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common;
using Vintagestory.Common;

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class AnimationPacket
{
    public long entityId;

    public int[] activeAnimations;
    public int activeAnimationsCount;
    public int activeAnimationsLength;
    public int[] activeAnimationSpeeds;
    public int activeAnimationSpeedsCount;
    public int activeAnimationSpeedsLength;

    public AnimationPacket()
    {

    }

    public AnimationPacket(Entity entity)
    {
        entityId = entity.EntityId;

        if (entity.AnimManager == null) return;
        Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode = entity.AnimManager.ActiveAnimationsByAnimCode;
        if (activeAnimationsByAnimCode.Count <= 0) return;
        int[] activeAnimationsArr = new int[activeAnimationsByAnimCode.Count];
        int[] activeAnimationSpeedsArr = new int[activeAnimationsByAnimCode.Count];
        int index = 0;
        foreach (KeyValuePair<string, AnimationMetaData> anim in activeAnimationsByAnimCode)
        {
            if (!(anim.Value.TriggeredBy?.DefaultAnim ?? false))
            {
                activeAnimationSpeedsArr[index] = CollectibleNet.SerializeFloatPrecise(anim.Value.AnimationSpeed); // Test not serializing this float.
                activeAnimationsArr[index++] = (int)anim.Value.CodeCrc32;
            }
        }

        activeAnimations = activeAnimationsArr;
        activeAnimationsCount = activeAnimationsArr.Length;
        activeAnimationsLength = activeAnimationsArr.Length;

        activeAnimationSpeeds = activeAnimationSpeedsArr;
        activeAnimationSpeedsCount = activeAnimationSpeedsArr.Length;
        activeAnimationSpeedsLength = activeAnimationSpeedsArr.Length;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class BulkAnimationPacket
{
    public AnimationPacket[] packets;
}