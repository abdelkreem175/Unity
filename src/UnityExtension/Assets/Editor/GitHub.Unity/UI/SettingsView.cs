using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace GitHub.Unity
{
    [Serializable]
    class SettingsView : Subview
    {
        private const string GitInstallTitle = "Git installation";
        private const string GitInstallBrowseTitle = "Select git binary";
        private const string GitInstallPickInvalidTitle = "Invalid Git install";
        private const string GitInstallPickInvalidMessage = "The selected file is not a valid Git install. {0}";
        private const string GitInstallPickInvalidOK = "OK";
        private const string GitInstallFindButton = "Find install";
        private const string GitRepositoryTitle = "Repository Configuration";
        private const string GitRepositoryRemoteLabel = "Remote";
        private const string GitRepositorySave = "Save Repository";
        private const string DebugSettingsTitle = "Debug";
        private const string PrivacyTitle = "Privacy";
        private const string EnableTraceLoggingLabel = "Enable Trace Logging";
        private const string MetricsOptInLabel = "Help us improve by sending anonymous usage data";
        private const string DefaultRepositoryRemoteName = "origin";

        [NonSerialized] private int newGitIgnoreRulesSelection = -1;

        [SerializeField] private int gitIgnoreRulesSelection = 0;
        [SerializeField] private string initDirectory;
        [SerializeField] private List<GitLock> lockedFiles = new List<GitLock>();
        [SerializeField] private Vector2 lockScrollPos;
        [SerializeField] private string repositoryRemoteName;
        [SerializeField] private string repositoryRemoteUrl;
        [SerializeField] private Vector2 scroll;
        [SerializeField] private bool isBusy;
        [SerializeField] private int lockedFileSelection = -1;
        [SerializeField] private bool hasRemote;
        [SerializeField] private bool remoteHasChanged;

        [SerializeField] private string newGitName;
        [SerializeField] private string newGitEmail;
        [SerializeField] private string newRepositoryRemoteUrl;
        [SerializeField] private User cachedUser;
        [SerializeField] private UserSettingsView userSettingsView = new UserSettingsView();

        public override void InitializeView(IView parent)
        {
            base.InitializeView(parent);
            userSettingsView.InitializeView(this);
        }

        public override void OnEnable()
        {
            base.OnEnable();
            userSettingsView.OnEnable();
            AttachHandlers(Repository);

            remoteHasChanged = true;
        }

        public override void OnDisable()
        {
            base.OnDisable();
            userSettingsView.OnDisable();
            DetachHandlers(Repository);
        }

        public override void OnDataUpdate()
        {
            base.OnDataUpdate();
            if (userSettingsView != null)
            {
                userSettingsView.OnDataUpdate();
            }

            MaybeUpdateData();
        }

        public override void OnRepositoryChanged(IRepository oldRepository)
        {
            base.OnRepositoryChanged(oldRepository);
            userSettingsView.OnRepositoryChanged(oldRepository);

            DetachHandlers(oldRepository);
            AttachHandlers(Repository);

            remoteHasChanged = true;

            Refresh();
        }

        public override void Refresh()
        {
            base.Refresh();
            userSettingsView.Refresh();
            if (Repository != null && Repository.CurrentRemote.HasValue)
            {
                Repository.ListLocks().Start();
            }
        }

        private void AttachHandlers(IRepository repository)
        {
            if (repository == null)
                return;

            repository.OnActiveRemoteChanged += Repository_OnActiveRemoteChanged;
            repository.OnLocksUpdated += RunLocksUpdateOnMainThread;
        }

        private void DetachHandlers(IRepository repository)
        {
            if (repository == null)
                return;

            repository.OnActiveRemoteChanged -= Repository_OnActiveRemoteChanged;
            repository.OnLocksUpdated -= RunLocksUpdateOnMainThread;
        }

        public override void OnGUI()
        {
            scroll = GUILayout.BeginScrollView(scroll);
            {
                userSettingsView.OnGUI();

                GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                if (Repository != null)
                {
                    OnRepositorySettingsGUI();

                    GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                    OnGitLfsLocksGUI();

                    GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                }

                OnInstallPathGUI();
                OnPrivacyGui();
                OnLoggingSettingsGui();
            }

            GUILayout.EndScrollView();
        }

        private void MaybeUpdateData()
        {
            if (lockedFiles == null)
                lockedFiles = new List<GitLock>();

            if (Repository == null)
                return;

            if (!remoteHasChanged)
                return;

            remoteHasChanged = false;
            var activeRemote = Repository.CurrentRemote;
            hasRemote = activeRemote.HasValue && !String.IsNullOrEmpty(activeRemote.Value.Url);
            if (!hasRemote)
            {
                repositoryRemoteName = DefaultRepositoryRemoteName;
                newRepositoryRemoteUrl = repositoryRemoteUrl = string.Empty;
            }
            else
            {
                repositoryRemoteName = activeRemote.Value.Name;
                newRepositoryRemoteUrl = repositoryRemoteUrl = activeRemote.Value.Url;
            }
        private void Repository_OnActiveRemoteChanged(string remote)
        {
            remoteHasChanged = true;
        }

        private void RunLocksUpdateOnMainThread(IEnumerable<GitLock> locks)
        {
            new ActionTask(TaskManager.Token, _ => OnLocksUpdate(locks))
                .ScheduleUI(TaskManager);
        }

        private void OnLocksUpdate(IEnumerable<GitLock> update)
        {
            if (update == null)
            {
                return;
            }
            lockedFiles = update.ToList();
            if (lockedFiles.Count <= lockedFileSelection)
            {
                lockedFileSelection = -1;
            }
            Redraw();
        }

        private void OnRepositorySettingsGUI()
        {
            GUILayout.Label(GitRepositoryTitle, EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(isBusy);
            {
                newRepositoryRemoteUrl = EditorGUILayout.TextField(GitRepositoryRemoteLabel + ": " + repositoryRemoteName, newRepositoryRemoteUrl);
                var needsSaving = newRepositoryRemoteUrl != repositoryRemoteUrl && !String.IsNullOrEmpty(newRepositoryRemoteUrl);
                EditorGUI.BeginDisabledGroup(!needsSaving);
                {
                    if (GUILayout.Button(GitRepositorySave, GUILayout.ExpandWidth(false)))
                    {
                        try
                        {
                            isBusy = true;
                            Repository.SetupRemote(repositoryRemoteName, newRepositoryRemoteUrl)
                                .FinallyInUI((_, __) =>
                                {
                                    isBusy = false;
                                    Redraw();
                                })
                                .Start();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex);
                        }
                    }
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUI.EndDisabledGroup();
        }

        private bool ValidateGitInstall(string path)
        {
            if (String.IsNullOrEmpty(path))
                return false;
            if (!GitClient.ValidateGitInstall(path.ToNPath()))
            {
                EditorUtility.DisplayDialog(GitInstallPickInvalidTitle, String.Format(GitInstallPickInvalidMessage, path),
                    GitInstallPickInvalidOK);
                return false;
            }

            return true;
        }

        private void OnGitLfsLocksGUI()
        {
            EditorGUI.BeginDisabledGroup(isBusy || Repository == null);
            {
                GUILayout.BeginVertical();
                {
                    GUILayout.Label("Locked files", EditorStyles.boldLabel);

                    lockScrollPos = EditorGUILayout.BeginScrollView(lockScrollPos, Styles.GenericTableBoxStyle,
                        GUILayout.Height(125));
                    {
                        GUILayout.BeginVertical();
                        {
                            var lockedFilesCount = lockedFiles.Count;
                            for (var index = 0; index < lockedFilesCount; ++index)
                            {
                                GUIStyle rowStyle = (lockedFileSelection == index)
                                    ? Styles.LockedFileRowSelectedStyle
                                    : Styles.LockedFileRowStyle;
                                GUILayout.Box(lockedFiles[index].Path, rowStyle);

                                if (Event.current.type == EventType.MouseDown &&
                                    GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                                {
                                    var currentEvent = Event.current;

                                    if (currentEvent.button == 0)
                                    {
                                        lockedFileSelection = index;
                                    }

                                    Event.current.Use();
                                }
                            }
                        }

                        GUILayout.EndVertical();
                    }

                    EditorGUILayout.EndScrollView();

                    if (lockedFileSelection > -1)
                    {
                        GUILayout.BeginVertical();
                        {
                            var lck = lockedFiles[lockedFileSelection];
                            GUILayout.Label(lck.Path, EditorStyles.boldLabel);

                            GUILayout.BeginHorizontal();
                            {
                                GUILayout.Label("Locked by " + lck.User);
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button("Unlock"))
                                {
                                    Repository.ReleaseLock(lck.Path, false).Start();
                                }
                            }
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.EndVertical();
                    }
                }

                GUILayout.EndVertical();

            }
            EditorGUI.EndDisabledGroup();
        }

        private void OnInstallPathGUI()
        {
            string gitExecPath = null;
            string extension = null;
            string gitInstallPath = null;
            if (Environment != null)
            {
                extension = Environment.ExecutableExtension;
                if (Environment.IsWindows)
                {
                    extension = extension.TrimStart('.');
                }

                gitInstallPath = Environment.GitInstallPath;

                if (Environment.GitExecutablePath != null)
                    gitExecPath = Environment.GitExecutablePath.ToString();
            }

            // Install path
            GUILayout.Label(GitInstallTitle, EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(isBusy || gitExecPath == null);
            {
                // Install path field
                EditorGUI.BeginChangeCheck();
                {
                    //TODO: Verify necessary value for a non Windows OS
                    Styles.PathField(ref gitExecPath,
                        () => EditorUtility.OpenFilePanel(GitInstallBrowseTitle,
                            gitInstallPath,
                            extension), ValidateGitInstall);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    Logger.Trace("Setting GitExecPath: " + gitExecPath);

                    Manager.SystemSettings.Set(Constants.GitInstallPathKey, gitExecPath);
                    Environment.GitExecutablePath = gitExecPath.ToNPath();
                }

                GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                GUILayout.BeginHorizontal();
                {
                    // Find button - for attempting to locate a new install
                    if (GUILayout.Button(GitInstallFindButton, GUILayout.ExpandWidth(false)))
                    {
                        var task = new ProcessTask<NPath>(Manager.CancellationToken, new FirstLineIsPathOutputProcessor())
                            .Configure(Manager.ProcessManager, Environment.IsWindows ? "where" : "which", "git")
                            .FinallyInUI((success, ex, path) =>
                            {
                                if (success && !string.IsNullOrEmpty(path))
                                {
                                    Environment.GitExecutablePath = path;
                                    GUIUtility.keyboardControl = GUIUtility.hotControl = 0;
                                }
                            });
                    }
                }
                GUILayout.EndHorizontal();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void OnPrivacyGui()
        {
            var service = Manager != null && Manager.UsageTracker != null ? Manager.UsageTracker : null;

            GUILayout.Label(PrivacyTitle, EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(isBusy || service == null);
            {
                var metricsEnabled = service != null ? service.Enabled : false;
                EditorGUI.BeginChangeCheck();
                {
                    metricsEnabled = GUILayout.Toggle(metricsEnabled, MetricsOptInLabel);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    Manager.UsageTracker.Enabled = metricsEnabled;
                }

            }
            EditorGUI.EndDisabledGroup();
        }

        private void OnLoggingSettingsGui()
        {
            GUILayout.Label(DebugSettingsTitle, EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(isBusy);
            {
                var traceLogging = Logging.TracingEnabled;

                EditorGUI.BeginChangeCheck();
                {
                    traceLogging = GUILayout.Toggle(traceLogging, EnableTraceLoggingLabel);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    Logging.TracingEnabled = traceLogging;
                    Manager.UserSettings.Set(Constants.TraceLoggingKey, traceLogging);
                }
            }
            EditorGUI.EndDisabledGroup();
        }
    }
}
