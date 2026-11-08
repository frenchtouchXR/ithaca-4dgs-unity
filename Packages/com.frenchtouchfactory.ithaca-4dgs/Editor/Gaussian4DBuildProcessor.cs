// SPDX-License-Identifier: MIT

using GaussianSplatting4D.Runtime;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting4D.Editor
{
    // A la construction d'une application, rattache a chaque Gaussian4DPlayer son temporalDataAsset
    // (<nomAsset>_tmp.bytes) s'il est vide : la recherche passe par AssetDatabase, absente en build.
    class Gaussian4DBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) // passage en mode Play : l'editeur s'en charge deja
                return;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var player in root.GetComponentsInChildren<Gaussian4DPlayer>(true))
            {
                player.AutoPopulateTemporalDataAsset();
                if (player.temporalDataAsset == null && player.GetComponent<Gaussian4DSplatRenderer>()?.m_Asset != null)
                    Debug.LogWarning($"[Gaussian4DPlayer] {player.name} : aucun Temporal Data Asset trouve, la sequence ne sera pas animee dans l'application", player);
            }
        }
    }
}
