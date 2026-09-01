using IPA;
using UnityEngine;
using IPALogger = IPA.Logging.Logger;

namespace BeatAvatars
{
    /// <summary>
    /// Your avatar as your in-game body, built on the game's OWN avatar system, not on custom models.
    ///
    /// Three small pieces bolted onto public API that already exists:
    ///   * <see cref="BeatSaber.AvatarCore.IAvatarSystem.InstantiateAvatar"/> spawns the same
    ///     BeatAvatar the game shows other players in multiplayer;
    ///   * <see cref="LocalPlayerPoseProvider"/> feeds it the local head and hands instead of a
    ///     network peer's;
    ///   * <see cref="AvatarLayers"/> keeps your own head out of your own eyes.
    ///
    /// There is deliberately no IK, no model loading and no calibration. The BeatAvatar is a head,
    /// two floating hands and a body placed from a fixed neck offset -- that is the entire rig.
    /// </summary>
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }

        [Init]
        public Plugin(IPALogger logger)
        {
            Log = logger;
        }

        [OnStart]
        public void OnStart()
        {
            // Says OnStart ran at all. Without it, a controller that stalls before its own first
            // log is indistinguishable from a plugin that never started.
            Log.Info("Starting.");

            var host = new GameObject("BeatAvatars");
            Object.DontDestroyOnLoad(host);

            host.AddComponent<BeatAvatarsController>();
            host.AddComponent<UI.BeatAvatarsMenu>();
        }
    }
}
