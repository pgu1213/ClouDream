using UnityEngine;

namespace ClouDream.LostSkies
{
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Weather")]
    public sealed class CloudWeatherProfile : ScriptableObject
    {
        public CloudEnvironment environment = CloudEnvironment.ClearDay();
    }
}
