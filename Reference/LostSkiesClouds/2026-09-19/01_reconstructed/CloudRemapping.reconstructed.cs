// MANUAL RECONSTRUCTION FROM IL2CPP/ISIL -- NOT ORIGINAL SOURCE.
// Confidence: HIGH. See 00_report/MANUAL_RECONSTRUCTION_NOTICE_KO.md.

using Expanse;
using UnityEngine;

namespace _game.code.vfx;

public partial class CloudRemapping
{
    private void Start()
    {
        if (_planet == null)
            _planet = Object.FindAnyObjectByType<GlobalSettings>();

        _floatingWorldOriginService.FloatingWorldOriginShifted +=
            FloatingWorldOriginShifted;
    }

    private void FloatingWorldOriginShifted(Vector3 obj)
    {
        _planet.m_planetOriginOffset -= obj;

        if (!CloudManager.IsManagerInSceneLoaded())
            return;

        CloudManager manager = CloudManager.GetSceneManager();
        if (manager != null)
            manager.planetOriginOffset -= obj;
    }
}
