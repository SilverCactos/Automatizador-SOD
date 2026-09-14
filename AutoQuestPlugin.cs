using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace SoDAutoQuestMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class AutoQuestPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.usuario.sod.autoquest";
        public const string PLUGIN_NAME = "SoD Auto Quest";
        public const string PLUGIN_VERSION = "1.9.0";

        // Atalhos
        private KeyCode keyAutoComplete = KeyCode.F8;      // F8: Forçar conclusão da missão atual
        private KeyCode keyToggleAutoMode = KeyCode.F9;    // F9: Liga/Desliga modo contínuo
        private KeyCode keyUnlockPlayer = KeyCode.F10;     // F10: Destravar HUD/Controles
        private bool isAutoModeActive = false;

        private float autoTimer = 0f;
        private const float AUTO_INTERVAL = 1.0f; // Intervalo ágil de 1.0s

        private float taskRetryTimer = 0f;
        private int currentProcessingTaskId = -1;

        private MethodInfo onCommandSubmittedMethod = null;

        private void Awake()
        {
            Logger.LogInfo($"Mod {PLUGIN_NAME} v{PLUGIN_VERSION} carregado com sucesso!");
        }

        private void Update()
        {
            // F8: Forçar conclusão imediata
            if (Input.GetKeyDown(keyAutoComplete))
            {
                currentProcessingTaskId = -1;
                taskRetryTimer = 0f;
                ForceCompleteCurrentTask();
                DismissDialogsAndPopups();
                RestorePlayerAndHUD();
            }

            // F9: Liga/Desliga modo contínuo
            if (Input.GetKeyDown(keyToggleAutoMode))
            {
                isAutoModeActive = !isAutoModeActive;
                currentProcessingTaskId = -1;
                taskRetryTimer = 0f;
                Logger.LogInfo($"Modo Auto-Quest: {(isAutoModeActive ? "ATIVADO" : "DESATIVADO")}");

                if (!isAutoModeActive)
                {
                    RestorePlayerAndHUD();
                }
            }

            // F10: Destravar HUD e Movimento manualmente se desejar
            if (Input.GetKeyDown(keyUnlockPlayer))
            {
                RestorePlayerAndHUD();
                Logger.LogInfo("[AutoQuest] F10: HUD e Controles restaurados.");
            }

            // Loop Automático Contínuo
            if (isAutoModeActive)
            {
                autoTimer += Time.deltaTime;
                taskRetryTimer += Time.deltaTime;

                if (autoTimer >= AUTO_INTERVAL)
                {
                    autoTimer = 0f;
                    
                    ProcessActiveTasks();
                    DismissDialogsAndPopups();
                    RestorePlayerAndHUD();
                }
            }
        }

        /// <summary>
        /// Restaura a movimentação, câmera e a barra de interface (UiToolbar)
        /// </summary>
        private void RestorePlayerAndHUD()
        {
            try
            {
                if (AvAvatar.pState == AvAvatarState.PAUSED || AvAvatar.pState == AvAvatarState.NONE || AvAvatar.pState == AvAvatarState.TASKREWARD)
                {
                    AvAvatar.pState = AvAvatarState.IDLE;
                }
                AvAvatar.pInputEnabled = true;
                AvAvatar.EnableAllInputs(true);
                AvAvatar.SetUIActive(true);

                UiToolbar toolbar = UnityEngine.Object.FindObjectOfType<UiToolbar>();
                if (toolbar != null && !toolbar.GetVisibility())
                {
                    toolbar.SetVisibility(true);
                    toolbar.SetInteractive(true);
                }

                KAUICursorManager.SetDefaultCursor("Arrow", true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoQuest] Aviso ao restaurar HUD: {ex.Message}");
            }
        }

        /// <summary>
        /// Força a conclusão imediata (usado pelo F8)
        /// </summary>
        private void ForceCompleteCurrentTask()
        {
            if (MissionManager.pInstance == null) return;
            List<Task> activeTasks = MissionManager.pInstance.pActiveTasks;

            if (activeTasks != null && activeTasks.Count > 0)
            {
                for (int i = 0; i < activeTasks.Count; i++)
                {
                    Task task = activeTasks[i];
                    if (task != null && !task.pCompleted)
                    {
                        Logger.LogInfo($"[AutoQuest] (F8) Forçando 'task complete {task.TaskID}' ({task.Name})");
                        ExecuteConsoleCommand($"task complete {task.TaskID}");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Processa a missão ativa com reenvio caso ela continue ativa após 2.5s
        /// </summary>
        private void ProcessActiveTasks()
        {
            if (MissionManager.pInstance == null) return;
            List<Task> activeTasks = MissionManager.pInstance.pActiveTasks;

            if (activeTasks == null || activeTasks.Count == 0)
            {
                currentProcessingTaskId = -1;
                return;
            }

            for (int i = 0; i < activeTasks.Count; i++)
            {
                Task task = activeTasks[i];
                if (task != null && !task.pCompleted)
                {
                    if (task.TaskID != currentProcessingTaskId || taskRetryTimer >= 2.5f)
                    {
                        currentProcessingTaskId = task.TaskID;
                        taskRetryTimer = 0f;

                        Logger.LogInfo($"[AutoQuest] Enviando comando: task complete {task.TaskID} ({task.Name})");
                        ExecuteConsoleCommand($"task complete {task.TaskID}");
                    }
                    break;
                }
            }
        }

        private void ExecuteConsoleCommand(string command)
        {
            try
            {
                if (onCommandSubmittedMethod == null)
                {
                    Type consoleType = Type.GetType("BTConsole.Console, BTConsole");
                    if (consoleType != null)
                    {
                        onCommandSubmittedMethod = consoleType.GetMethod(
                            "OnCommandSubmitted",
                            BindingFlags.NonPublic | BindingFlags.Static
                        );
                    }
                }

                if (onCommandSubmittedMethod != null)
                {
                    onCommandSubmittedMethod.Invoke(null, new object[] { command, false });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AutoQuest] Erro ao enviar comando '{command}': {ex.Message}");
            }
        }

        /// <summary>
        /// Aceita diálogos de NPC, ofertas de missões e confirma recompensas de forma 100% segura
        /// </summary>
        private void DismissDialogsAndPopups()
        {
            // 1. Janelas de diálogo de fala do NPC (UiMissionActionDB)
            try
            {
                UiMissionActionDB[] actionDBs = UnityEngine.Object.FindObjectsOfType<UiMissionActionDB>();
                if (actionDBs != null && actionDBs.Length > 0)
                {
                    foreach (var actionDB in actionDBs)
                    {
                        if (actionDB != null && actionDB.gameObject != null && actionDB.GetVisibility())
                        {
                            KAWidget btnNext = actionDB.FindItem("BtnNext");
                            if (btnNext != null && btnNext.GetVisibility()) actionDB.OnClick(btnNext);

                            KAWidget btnYes = actionDB.FindItem("BtnYes");
                            if (btnYes != null && btnYes.GetVisibility()) actionDB.OnClick(btnYes);

                            KAWidget btnOK = actionDB.FindItem("BtnOK");
                            if (btnOK != null && btnOK.GetVisibility()) actionDB.OnClick(btnOK);

                            KAWidget btnClose = actionDB.FindItem("BtnClose");
                            if (btnClose != null && btnClose.GetVisibility()) actionDB.OnClick(btnClose);
                        }
                    }
                }
            }
            catch {}

            // 2. Janela de detalhes de missão do NPC (UiNPCQuestDetails)
            try
            {
                UiNPCQuestDetails[] questDetails = UnityEngine.Object.FindObjectsOfType<UiNPCQuestDetails>();
                if (questDetails != null && questDetails.Length > 0)
                {
                    foreach (var details in questDetails)
                    {
                        if (details != null && details.gameObject != null && details.GetVisibility())
                        {
                            KAWidget btnAccept = details.FindItem("BtnAccept");
                            if (btnAccept != null && btnAccept.GetVisibility()) details.OnClick(btnAccept);

                            KAWidget btnStart = details.FindItem("BtnStart");
                            if (btnStart != null && btnStart.GetVisibility()) details.OnClick(btnStart);

                            KAWidget btnOK = details.FindItem("BtnOK");
                            if (btnOK != null && btnOK.GetVisibility()) details.OnClick(btnOK);

                            KAWidget btnClose = details.FindItem("BtnClose");
                            if (btnClose != null && btnClose.GetVisibility()) details.OnClick(btnClose);
                        }
                    }
                }
            }
            catch {}

            // 3. Pop-ups genéricos (KAUIGenericDB - Recompensas, avisos)
            try
            {
                KAUIGenericDB[] popups = UnityEngine.Object.FindObjectsOfType<KAUIGenericDB>();
                if (popups != null && popups.Length > 0)
                {
                    foreach (var popup in popups)
                    {
                        if (popup != null && popup.gameObject != null && popup.GetVisibility())
                        {
                            KAWidget btnOK = popup.FindItem("BtnOK");
                            if (btnOK != null && btnOK.GetVisibility()) popup.OnClick(btnOK);

                            KAWidget btnYes = popup.FindItem("BtnYes");
                            if (btnYes != null && btnYes.GetVisibility()) popup.OnClick(btnYes);

                            KAWidget btnClose = popup.FindItem("BtnClose");
                            if (btnClose != null && btnClose.GetVisibility()) popup.OnClick(btnClose);
                        }
                    }
                }
            }
            catch {}

            // 4. MissionManager (encerrar cutscenes se pendentes)
            try
            {
                if (MissionManager.pInstance != null)
                {
                    if (MissionManager.MissionActionPending())
                    {
                        MissionManager.pInstance.EndAction();
                    }
                }
            }
            catch {}
        }

        private void OnGUI()
        {
            if (isAutoModeActive)
            {
                GUI.color = Color.green;
                GUI.Label(new Rect(10, 10, 450, 25), "[Auto-Quest ATIVADO e Monitorando... (F9 para parar)]");
            }
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.usuario.sod.autoquest";
        public const string PLUGIN_NAME = "SoD Auto Quest";
        public const string PLUGIN_VERSION = "1.9.0";
    }
}