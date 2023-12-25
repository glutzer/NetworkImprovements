using HarmonyLib;
using Vintagestory.GameContent;

public class Patches
{
    /// <summary>
    /// Patch this one AI Task to also try to get step height from the new physics.
    /// </summary>
    [HarmonyPatch(typeof(AiTaskBaseTargetable), "StartExecute")]
    public static class StartExecutePostfix
    {
        [HarmonyPostfix]
        public static void Postfix(AiTaskBaseTargetable __instance)
        {
            //Use this physics step height instead if it exists
            EntityControlledPhysics physics = __instance.entity.GetBehavior<EntityControlledPhysics>();
            if (physics != null)
            {
                __instance.SetField("stepHeight", physics.stepHeight);
            }
        }
    }
}