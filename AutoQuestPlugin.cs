using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace SoDAutoQuestMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class AutoQuestPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.usuario.sod.autoquest";
        public const string PLUGIN_NAME = "SoD Auto Quest";
        public const string PLUGIN_VERSION = "2.1.1";

        // Ative para logar no console do BepInEx quais widgets existem em cada diálogo
        // visível. Útil para descobrir o nome exato dos botões caso algum diálogo
        // continue não sendo fechado automaticamente.
        private const bool DEBUG_LOG_WIDGETS = false;

        // ---------------- Atalhos ----------------
        private readonly KeyCode keyAutoComplete = KeyCode.F8;    // F8: Forçar conclusão da missão atual
        private readonly KeyCode keyToggleAutoMode = KeyCode.F9;  // F9: Liga/Desliga modo contínuo
        private readonly KeyCode keyUnlockPlayer = KeyCode.F10;   // F10: Destravar HUD/Controles manualmente

        private bool isAutoModeActive = false;

        // ---------------- Timers ----------------
        // Diálogos/cutscenes são checados com bastante frequência para nunca "prender" a tela.
        private const float DIALOG_CHECK_INTERVAL = 0.25f;
        private float dialogCheckTimer = 0f;

        // Progresso de missão é checado a cada 1s (reenvia o comando se a missão travar).
        private const float TASK_CHECK_INTERVAL = 1.0f;
        private const float TASK_RETRY_INTERVAL = 2.5f;
        private float taskCheckTimer = 0f;
        private float taskRetryTimer = 0f;
        private int currentProcessingTaskId = -1;

        // Se não houver NENHUMA atividade (sem missão ativa, sem diálogo fechado, sem
        // cutscene encerrada) por esse tempo seguido, o modo automático se desliga sozinho.
        // Fechar um diálogo ou encerrar uma cutscene reinicia essa contagem, porque isso
        // costuma ser sinal de que uma nova missão está prestes a aparecer.
        private const float NO_TASKS_GRACE_PERIOD = 8f;
        private float noTasksTimer = 0f;

        // ---------------- Reflection (console) ----------------
        private MethodInfo onCommandSubmittedMethod = null;
        private bool consoleLookupFailedLogged = false;

        // Nomes candidatos de botão. Vários nomes são tentados porque diálogos diferentes
        // (oferta de missão, entrega de recompensa, fala de NPC) nem sempre usam o mesmo id.
        private static readonly string[] AcceptButtonNames =
        {
            "BtnAccept", "BtnYes", "BtnStart", "BtnOK", "BtnConfirm",
            "BtnContinue", "BtnClaim", "BtnCollect", "BtnTurnIn", "BtnDone"
        };

        private static readonly string[] CloseButtonNames =
        {
            "BtnNext", "BtnClose", "BtnOK", "BtnSkip", "BtnCancel"
        };

        private void Awake()
        {
            Logger.LogInfo($"Mod {PLUGIN_NAME} v{PLUGIN_VERSION} carregado com sucesso!");
        }

        private void Update()
        {
            HandleHotkeys();

            if (!isAutoModeActive) return;

            // 1) Diálogos e cutscenes: checagem rápida e constante, para nunca travar a tela.
            dialogCheckTimer += Time.deltaTime;
            if (dialogCheckTimer >= DIALOG_CHECK_INTERVAL)
            {
                dialogCheckTimer = 0f;
                bool activityDetected = DismissDialogsAndPopups();

                // Fechar um diálogo ou encerrar uma cutscene é sinal de que o jogo ainda
                // está progredindo — isso "reinicia o relógio" do desligamento automático,
                // mesmo que pActiveTasks esteja momentaneamente vazio.
                if (activityDetected)
                {
                    noTasksTimer = 0f;
                }
            }

            // 2) Progresso de missão: checagem um pouco mais espaçada.
            taskCheckTimer += Time.deltaTime;
            taskRetryTimer += Time.deltaTime;
            if (taskCheckTimer >= TASK_CHECK_INTERVAL)
            {
                taskCheckTimer = 0f;
                ProcessActiveTasks();

                // Só mexemos no estado do avatar/HUD fora de uma cutscene/ação de missão,
                // para não brigar com o próprio jogo e fazer a HUD sumir.
                if (!IsMissionActionPending())
                {
                    RestorePlayerAndHUD();
                }
            }
        }

        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(keyAutoComplete))
            {
                currentProcessingTaskId = -1;
                taskRetryTimer = 0f;
                ForceCompleteCurrentTask();
                DismissDialogsAndPopups();
                if (!IsMissionActionPending())
                {
                    RestorePlayerAndHUD();
                }
            }

            if (Input.GetKeyDown(keyToggleAutoMode))
            {
                SetAutoMode(!isAutoModeActive);
            }

            if (Input.GetKeyDown(keyUnlockPlayer))
            {
                RestorePlayerAndHUD();
                Logger.LogInfo("[AutoQuest] F10: HUD e Controles restaurados.");
            }
        }

        private void SetAutoMode(bool active)
        {
            isAutoModeActive = active;
            currentProcessingTaskId = -1;
            taskRetryTimer = 0f;
            noTasksTimer = 0f;
            dialogCheckTimer = 0f;
            taskCheckTimer = 0f;

            Logger.LogInfo($"[AutoQuest] Modo Auto-Quest: {(isAutoModeActive ? "ATIVADO" : "DESATIVADO")}");

            if (!isAutoModeActive)
            {
                RestorePlayerAndHUD();
            }
        }

        /// <summary>
        /// Restaura movimentação, câmera e a barra de interface (UiToolbar).
        /// Nunca esconde nada — só garante que o que deveria estar visível, esteja.
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

        private bool IsMissionActionPending()
        {
            try
            {
                return MissionManager.pInstance != null && MissionManager.MissionActionPending();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Força a conclusão imediata da missão atual (usado pelo F8).
        /// </summary>
        private void ForceCompleteCurrentTask()
        {
            if (MissionManager.pInstance == null) return;
            List<Task> activeTasks = MissionManager.pInstance.pActiveTasks;
            if (activeTasks == null || activeTasks.Count == 0) return;

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

        /// <summary>
        /// Processa a missão ativa, reenviando o comando se ela continuar ativa depois
        /// de TASK_RETRY_INTERVAL segundos. Quando não há mais nenhuma missão ativa por
        /// tempo suficiente, desliga o modo automático sozinho.
        /// </summary>
        private void ProcessActiveTasks()
        {
            if (MissionManager.pInstance == null) return;
            List<Task> activeTasks = MissionManager.pInstance.pActiveTasks;

            if (activeTasks == null || activeTasks.Count == 0)
            {
                currentProcessingTaskId = -1;
                noTasksTimer += TASK_CHECK_INTERVAL;

                if (noTasksTimer >= NO_TASKS_GRACE_PERIOD)
                {
                    Logger.LogInfo("[AutoQuest] Nenhuma missão ativa encontrada. Encerrando o modo automático.");
                    SetAutoMode(false);
                }
                return;
            }

            noTasksTimer = 0f;

            for (int i = 0; i < activeTasks.Count; i++)
            {
                Task task = activeTasks[i];
                if (task != null && !task.pCompleted)
                {
                    if (task.TaskID != currentProcessingTaskId || taskRetryTimer >= TASK_RETRY_INTERVAL)
                    {
                        currentProcessingTaskId = task.TaskID;
                        taskRetryTimer = 0f;

                        Logger.LogInfo($"[AutoQuest] Enviando comando: task complete {task.TaskID} ({task.Name})");
                        ExecuteConsoleCommand($"task complete {task.TaskID}");
                    }
                    return;
                }
            }
        }

        private void ExecuteConsoleCommand(string command)
        {
            try
            {
                if (onCommandSubmittedMethod == null)
                {
                    onCommandSubmittedMethod = ResolveConsoleCommandMethod();
                    if (onCommandSubmittedMethod == null)
                    {
                        if (!consoleLookupFailedLogged)
                        {
                            consoleLookupFailedLogged = true;
                            Logger.LogError(
                                "[AutoQuest] Não foi possível localizar o método do BTConsole por reflection. " +
                                "Nenhum comando será enviado e nenhuma missão vai avançar até isso ser corrigido. " +
                                "Confirme se BTConsole.dll está em BepInEx/plugins e se o mod está referenciando a versão correta.");
                        }
                        return;
                    }
                }

                onCommandSubmittedMethod.Invoke(null, new object[] { command, false });
            }
            catch (TargetInvocationException tie)
            {
                Logger.LogError($"[AutoQuest] Erro ao executar comando '{command}': {tie.InnerException?.Message ?? tie.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AutoQuest] Erro ao enviar comando '{command}': {ex.Message}");
            }
        }

        /// <summary>
        /// Procura o método estático responsável por processar comandos do BTConsole.
        /// Tenta algumas variações de nome de tipo/método porque isso pode mudar entre versões do BTConsole.
        /// </summary>
        private MethodInfo ResolveConsoleCommandMethod()
        {
            string[] typeCandidates =
            {
                "BTConsole.Console, BTConsole",
                "BTConsole.BTConsole, BTConsole",
                "BTConsole.ConsoleController, BTConsole"
            };

            string[] methodNameCandidates = { "OnCommandSubmitted", "SubmitCommand", "ExecuteCommand", "RunCommand" };

            foreach (string typeName in typeCandidates)
            {
                Type consoleType = Type.GetType(typeName);
                if (consoleType == null) continue;

                foreach (string methodName in methodNameCandidates)
                {
                    MethodInfo mi = consoleType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
                                 ?? consoleType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);

                    if (mi != null)
                    {
                        Logger.LogInfo($"[AutoQuest] Console encontrado via reflection: {typeName}.{methodName}");
                        return mi;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Fecha diálogos de NPC, aceita ofertas de missão e confirma recompensas.
        /// Também encerra cutscenes/ações de missão pendentes para não prender a tela.
        /// Roda com alta frequência (DIALOG_CHECK_INTERVAL) para não deixar nada "preso".
        ///
        /// Em vez de depender só de nomes de botão "chutados" (que podem não bater com
        /// o jogo real e deixar tudo travado), cada diálogo visível agora é varrido:
        /// se nenhum dos nomes conhecidos funcionar, procuramos entre TODOS os widgets
        /// filhos por algo que pareça um botão de ação positiva. Se mesmo assim nada for
        /// clicado, listamos os widgets encontrados no log para diagnóstico.
        /// </summary>
        /// <returns>
        /// true se algum diálogo foi clicado, alguma cutscene foi encerrada, ou o
        /// fallback de Button clicou em algo — ou seja, se houve qualquer sinal de que
        /// o jogo ainda está progredindo.
        /// </returns>
        private bool DismissDialogsAndPopups()
        {
            bool clickedSomething = false;

            clickedSomething |= HandleMissionActionDialogs();
            clickedSomething |= HandleQuestDetailDialogs();
            clickedSomething |= HandleGenericPopups();

            // Cutscenes/ações de missão pendentes — chamado a cada checagem para não
            // deixar a cutscene "congelada" esperando múltiplos avanços.
            try
            {
                if (MissionManager.pInstance != null && MissionManager.MissionActionPending())
                {
                    MissionManager.pInstance.EndAction();
                    clickedSomething = true;
                }
            }
            catch { }

            // Último recurso: se nenhum diálogo "conhecido" foi clicado, procura por
            // qualquer Button (UGUI) visível/interativo com nome de ação positiva
            // (Accept/Yes/OK/Confirm/Claim/Continue/...) e clica nele diretamente.
            // Isso cobre o caso de o diálogo travado ser de um tipo que o mod não conhece.
            if (!clickedSomething)
            {
                clickedSomething |= TryClickAnyPositiveUnityButton();
            }

            return clickedSomething;
        }

        private bool HandleMissionActionDialogs()
        {
            bool clicked = false;
            try
            {
                UiMissionActionDB[] dbs = UnityEngine.Object.FindObjectsOfType<UiMissionActionDB>();
                if (dbs == null) return false;

                foreach (var db in dbs)
                {
                    if (db == null || db.gameObject == null || !db.GetVisibility()) continue;

                    KAWidget target = FindBestButtonExact(n => db.FindItem(n), CloseButtonNames)
                                    ?? FindBestButtonExact(n => db.FindItem(n), AcceptButtonNames)
                                    ?? FindBestButtonInChildren(db.gameObject);

                    if (target != null)
                    {
                        db.OnClick(target);
                        clicked = true;
                        if (DEBUG_LOG_WIDGETS) Logger.LogInfo($"[AutoQuest][DEBUG] UiMissionActionDB: clicou em '{target.name}'");
                    }
                    else
                    {
                        LogAllVisibleWidgets("UiMissionActionDB", db.gameObject);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoQuest] Erro em HandleMissionActionDialogs: {ex.Message}");
            }
            return clicked;
        }

        private bool HandleQuestDetailDialogs()
        {
            bool clicked = false;
            try
            {
                UiNPCQuestDetails[] dbs = UnityEngine.Object.FindObjectsOfType<UiNPCQuestDetails>();
                if (dbs == null) return false;

                foreach (var db in dbs)
                {
                    if (db == null || db.gameObject == null || !db.GetVisibility()) continue;

                    KAWidget target = FindBestButtonExact(n => db.FindItem(n), AcceptButtonNames)
                                    ?? FindBestButtonExact(n => db.FindItem(n), CloseButtonNames)
                                    ?? FindBestButtonInChildren(db.gameObject);

                    if (target != null)
                    {
                        db.OnClick(target);
                        clicked = true;
                        if (DEBUG_LOG_WIDGETS) Logger.LogInfo($"[AutoQuest][DEBUG] UiNPCQuestDetails: clicou em '{target.name}'");
                    }
                    else
                    {
                        LogAllVisibleWidgets("UiNPCQuestDetails", db.gameObject);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoQuest] Erro em HandleQuestDetailDialogs: {ex.Message}");
            }
            return clicked;
        }

        private bool HandleGenericPopups()
        {
            bool clicked = false;
            try
            {
                KAUIGenericDB[] dbs = UnityEngine.Object.FindObjectsOfType<KAUIGenericDB>();
                if (dbs == null) return false;

                foreach (var db in dbs)
                {
                    if (db == null || db.gameObject == null || !db.GetVisibility()) continue;

                    KAWidget target = FindBestButtonExact(n => db.FindItem(n), AcceptButtonNames)
                                    ?? FindBestButtonExact(n => db.FindItem(n), CloseButtonNames)
                                    ?? FindBestButtonInChildren(db.gameObject);

                    if (target != null)
                    {
                        db.OnClick(target);
                        clicked = true;
                        if (DEBUG_LOG_WIDGETS) Logger.LogInfo($"[AutoQuest][DEBUG] KAUIGenericDB: clicou em '{target.name}'");
                    }
                    else
                    {
                        LogAllVisibleWidgets("KAUIGenericDB", db.gameObject);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoQuest] Erro em HandleGenericPopups: {ex.Message}");
            }
            return clicked;
        }

        // ---------------- Busca de botão dentro de um diálogo já identificado ----------------

        private KAWidget FindBestButtonExact(Func<string, KAWidget> findItem, string[] names)
        {
            foreach (var n in names)
            {
                KAWidget w = null;
                try { w = findItem(n); } catch { }
                if (w != null && SafeGetVisibility(w)) return w;
            }
            return null;
        }

        private KAWidget FindBestButtonInChildren(GameObject root)
        {
            KAWidget best = null;
            int bestScore = int.MinValue;
            try
            {
                KAWidget[] widgets = root.GetComponentsInChildren<KAWidget>(true);
                if (widgets == null) return null;

                foreach (var w in widgets)
                {
                    if (w == null || !SafeGetVisibility(w)) continue;

                    var tokens = TokenizeCamelCase(w.name);
                    if (!LooksLikeButton(tokens)) continue;

                    int score = ScoreTokens(tokens);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = w;
                    }
                }
            }
            catch { }
            return best;
        }

        private bool SafeGetVisibility(KAWidget w)
        {
            try { return w.GetVisibility(); } catch { return false; }
        }

        private void LogAllVisibleWidgets(string label, GameObject root)
        {
            try
            {
                KAWidget[] widgets = root.GetComponentsInChildren<KAWidget>(true);
                if (widgets == null || widgets.Length == 0)
                {
                    Logger.LogWarning($"[AutoQuest] {label} visível mas nenhum KAWidget filho foi encontrado.");
                    return;
                }

                var parts = new List<string>();
                foreach (var w in widgets)
                {
                    if (w == null) continue;
                    parts.Add($"{w.name}(visivel={SafeGetVisibility(w)})");
                }

                Logger.LogWarning(
                    $"[AutoQuest] {label} visível, nenhum botão de ação foi reconhecido automaticamente. " +
                    $"Widgets encontrados: {string.Join(", ", parts)}. " +
                    "Envie essa linha de log para ajustar o mod com o nome exato do botão.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoQuest] Erro ao listar widgets de {label}: {ex.Message}");
            }
        }

        // ---------------- Fallback: clique direto em Button (UGUI) da cena ----------------

        private bool TryClickAnyPositiveUnityButton()
        {
            try
            {
                Button[] buttons = UnityEngine.Object.FindObjectsOfType<Button>();
                if (buttons == null) return false;

                Button best = null;
                int bestScore = int.MinValue;

                foreach (var btn in buttons)
                {
                    if (btn == null || !btn.isActiveAndEnabled || !btn.IsInteractable()) continue;

                    var tokens = TokenizeCamelCase(btn.name);
                    int score = ScoreTokens(tokens);

                    // Só considera candidatos com intenção positiva clara, para não
                    // clicar em botões aleatórios da interface (menu, opções, etc.).
                    if (score < 100) continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = btn;
                    }
                }

                if (best != null)
                {
                    Logger.LogInfo($"[AutoQuest] Fallback: clicando via UnityEngine.UI.Button em '{best.name}'.");
                    best.onClick.Invoke();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoQuest] Erro no fallback de Button: {ex.Message}");
            }
            return false;
        }

        // ---------------- Utilitários de nome/score de botão ----------------

        private static readonly HashSet<string> PositiveTokens = new HashSet<string>
        {
            "accept", "yes", "ok", "start", "confirm", "continue",
            "claim", "collect", "turnin", "next", "done", "get", "receive"
        };

        private static readonly HashSet<string> NegativeTokens = new HashSet<string>
        {
            "no", "decline", "cancel", "back", "reject"
        };

        private static bool LooksLikeButton(List<string> tokens)
        {
            foreach (var t in tokens)
                if (t == "btn" || t == "button") return true;
            return false;
        }

        private static int ScoreTokens(List<string> tokens)
        {
            bool positive = false, negative = false;
            foreach (var t in tokens)
            {
                if (PositiveTokens.Contains(t)) positive = true;
                if (NegativeTokens.Contains(t)) negative = true;
            }

            if (positive) return 100;
            if (negative) return -100;

            foreach (var t in tokens)
                if (t == "close") return 10;

            return 0;
        }

        /// <summary>
        /// Quebra um nome estilo "BtnAcceptQuest" ou "Btn_Accept_Quest" em tokens
        /// minúsculos: ["btn", "accept", "quest"]. Evita falsos positivos de simples
        /// Contains() (ex.: "no" dentro de "Notify").
        /// </summary>
        private static List<string> TokenizeCamelCase(string s)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(s)) return tokens;

            var current = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '_' || c == '-' || c == ' ')
                {
                    if (current.Length > 0) { tokens.Add(current.ToString().ToLowerInvariant()); current.Clear(); }
                    continue;
                }

                if (char.IsUpper(c) && current.Length > 0)
                {
                    tokens.Add(current.ToString().ToLowerInvariant());
                    current.Clear();
                }

                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString().ToLowerInvariant());
            return tokens;
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
        public const string PLUGIN_VERSION = "2.1.1";
    }
}