public interface IRemotePhysics
{
    public void HandleRemotePhysics(float dt, bool isTeleport);

    public void OnReceivedClientPos(int version, int tickDiff);
}
