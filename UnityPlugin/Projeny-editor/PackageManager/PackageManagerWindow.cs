using System;
using System.IO;
using UnityEditorInternal;
using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using Projeny.Internal;
using System.Linq;

namespace Projeny
{
    public class PackageManagerWindow : EditorWindow
    {
        DraggableList _installedList;
        DraggableList _releasesList;
        DraggableList _assetsList;
        DraggableList _pluginsList;

        List<PackageInfo> _allPackages;
        List<ReleaseInfo> _allReleases;

        ProjectConfigTypes _projectConfigType;

        ViewStates _viewState = ViewStates.PackagesAndProject;

        PackageManagerWindowSkin _skin;

        List<DraggableListEntry> _selected;

        Action<Rect> _popupHandler;

        ReleasesSortMethod _releasesSortMethod;

        bool _hasInitialized;
        bool _releaseSortAscending;

        [NonSerialized]
        float _split1 = 0;

        [NonSerialized]
        float _split2 = 0.5f;

        [NonSerialized]
        float _lastTime = 0.5f;

        [NonSerialized]
        BackgroundTaskInfo _backgroundTaskInfo;

        const string NotAvailableLabel = "N/A";

        PackageManagerWindowSkin Skin
        {
            get
            {
                return _skin ?? (_skin = Resources.Load<PackageManagerWindowSkin>("Projeny/PackageManagerSkin"));
            }
        }

        public IEnumerable<DraggableListEntry> Selected
        {
            get
            {
                return _selected;
            }
        }

        public void DrawItemLabel(Rect rect, DraggableListEntry entry)
        {
            switch (ClassifyList(entry.ListOwner))
            {
                case ListTypes.Release:
                {
                    var info = (ReleaseInfo)(entry.Tag);
                    DrawItemLabelWithVersion(rect, info.Name, info.Version);
                    break;
                }
                case ListTypes.Package:
                {
                    var info = (PackageInfo)(entry.Tag);

                    if (_viewState == ViewStates.ReleasesAndPackages)
                    {
                        DrawItemLabelWithVersion(rect, info.Name, TryGetVersion(info));
                    }
                    else
                    {
                        DrawListItem(rect, info.Name);
                    }

                    break;
                }
                case ListTypes.AssetItem:
                case ListTypes.PluginItem:
                {
                    DrawListItem(rect, entry.Name);
                    break;
                }
                default:
                {
                    Assert.Throw();
                    break;
                }
            }
        }

        string TryGetVersion(PackageInfo info)
        {
            if (info.InstallInfo == null || info.InstallInfo.ReleaseInfo == null)
            {
                return null;
            }

            return info.InstallInfo.ReleaseInfo.Version;
        }

        object GetReleaseSortField(DraggableListEntry entry)
        {
            var info = (ReleaseInfo)entry.Tag;

            switch (_releasesSortMethod)
            {
                case ReleasesSortMethod.Name:
                {
                    return info.Name;
                }
                case ReleasesSortMethod.Size:
                {
                    return info.CompressedSize;
                }
                case ReleasesSortMethod.PublishDate:
                {
                    return info.AssetStoreInfo == null ? 0 : info.AssetStoreInfo.PublishDateTicks;
                }
            }

            Assert.Throw();
            return null;
        }

        public List<DraggableListEntry> SortList(DraggableList list, List<DraggableListEntry> entries)
        {
            switch (ClassifyList(list))
            {
                case ListTypes.Release:
                {
                    if (_releaseSortAscending)
                    {
                        return entries.OrderBy(x => GetReleaseSortField(x)).ToList();
                    }

                    return entries.OrderByDescending(x => GetReleaseSortField(x)).ToList();
                }
                default:
                {
                    return entries.OrderBy(x => x.Name).ToList();
                }
            }
        }

        void DrawListItem(Rect rect, string name)
        {
            GUI.Label(rect, name, Skin.ItemTextStyle);
        }

        void DrawItemLabelWithVersion(Rect rect, string name, string version)
        {
            var labelStr = string.IsNullOrEmpty(version) ? name : "{0} <color=#{1}>v{2}</color>".Fmt(name, MiscUtil.ColorToHex(Skin.Theme.VersionColor), version);
            GUI.Label(rect, labelStr, Skin.ItemTextStyle);
        }

        public void ClearSelected()
        {
            _selected.Clear();
        }

        public void Select(DraggableListEntry newEntry)
        {
            if (_selected.Contains(newEntry))
            {
                if (Event.current.control)
                {
                    _selected.RemoveWithConfirm(newEntry);
                }

                return;
            }

            if (!Event.current.control && !Event.current.shift)
            {
                _selected.Clear();
            }

            // The selection entry list should all be from the same list
            foreach (var existingEntry in _selected.ToList())
            {
                if (existingEntry.ListOwner != newEntry.ListOwner)
                {
                    _selected.Remove(existingEntry);
                }
            }

            if (Event.current.shift && !_selected.IsEmpty())
            {
                var closestEntry = _selected.Select(x => new { Distance = Mathf.Abs(x.Index - newEntry.Index), Entry = x }).OrderBy(x => x.Distance).Select(x => x.Entry).First();

                int startIndex;
                int endIndex;

                if (closestEntry.Index > newEntry.Index)
                {
                    startIndex = newEntry.Index + 1;
                    endIndex = closestEntry.Index - 1;
                }
                else
                {
                    startIndex = closestEntry.Index + 1;
                    endIndex = newEntry.Index - 1;
                }

                for (int i = startIndex; i <= endIndex; i++)
                {
                    var inBetweenEntry = closestEntry.ListOwner.GetAtIndex(i);

                    if (!_selected.Contains(inBetweenEntry))
                    {
                        _selected.Add(inBetweenEntry);
                    }
                }
            }

            _selected.Add(newEntry);
        }

        void OnEnable()
        {
            if (!_hasInitialized)
            {
                _hasInitialized = true;

                Assert.IsNull(_selected);
                Assert.IsNull(_allPackages);
                Assert.IsNull(_allReleases);
                Assert.IsNull(_installedList);
                Assert.IsNull(_releasesList);
                Assert.IsNull(_assetsList);
                Assert.IsNull(_pluginsList);

                _selected = new List<DraggableListEntry>();
                _allPackages = new List<PackageInfo>();
                _allReleases = new List<ReleaseInfo>();

                SetBackgroundTask(RefreshAll(), true, "Refreshing Packages");
            }

            if (_installedList == null)
            {
                _installedList = ScriptableObject.CreateInstance<DraggableList>();
                _installedList.Manager = this;
            }

            if (_releasesList == null)
            {
                _releasesList = ScriptableObject.CreateInstance<DraggableList>();
                _releasesList.Manager = this;
            }

            if (_assetsList == null)
            {
                _assetsList = ScriptableObject.CreateInstance<DraggableList>();
                _assetsList.Manager = this;
            }

            if (_pluginsList == null)
            {
                _pluginsList = ScriptableObject.CreateInstance<DraggableList>();
                _pluginsList.Manager = this;
            }
        }

        IEnumerator RefreshAll()
        {
            RefreshProject();
            yield return RefreshPackagesAsync();
            yield return RefreshReleasesAsync();
        }

        float GetDesiredSplit1()
        {
            if (_viewState == ViewStates.ReleasesAndPackages)
            {
                return 0.5f;
            }

            return 0;
        }

        float GetDesiredSplit2()
        {
            if (_viewState == ViewStates.ReleasesAndPackages)
            {
                return 1.0f;
            }

            if (_viewState == ViewStates.Project)
            {
                return 0;
            }

            return 0.4f;
        }

        void Update()
        {
            if (_backgroundTaskInfo != null)
            {
                // NOTE: If the tab isn't focused this task will take awhile
                if (!_backgroundTaskInfo.CoRoutine.Pump())
                {
                    _backgroundTaskInfo = null;
                }
            }

            var deltaTime = Time.realtimeSinceStartup - _lastTime;
            _lastTime = Time.realtimeSinceStartup;

            var px = Mathf.Clamp(deltaTime * Skin.InterpSpeed, 0, 1);

            _split1 = Mathf.Lerp(_split1, GetDesiredSplit1(), px);
            _split2 = Mathf.Lerp(_split2, GetDesiredSplit2(), px);

            // Doesn't seem worth trying to detect changes, just redraw every frame
            Repaint();
        }

        public bool IsDragAllowed(DraggableList.DragData data, DraggableList list)
        {
            var sourceListType = ClassifyList(data.SourceList);
            var dropListType = ClassifyList(list);

            if (sourceListType == dropListType)
            {
                return true;
            }

            switch (dropListType)
            {
                case ListTypes.Package:
                {
                    return sourceListType == ListTypes.Release || sourceListType == ListTypes.AssetItem || sourceListType == ListTypes.PluginItem;
                }
                case ListTypes.Release:
                {
                    return false;
                }
                case ListTypes.AssetItem:
                {
                    return sourceListType == ListTypes.Package || sourceListType == ListTypes.PluginItem;
                }
                case ListTypes.PluginItem:
                {
                    return sourceListType == ListTypes.Package || sourceListType == ListTypes.AssetItem;
                }
            }

            Assert.Throw();
            return true;
        }

        public void OpenContextMenu(DraggableList sourceList)
        {
            var listType = ClassifyList(sourceList);

            GenericMenu contextMenu = new GenericMenu();

            switch (listType)
            {
                case ListTypes.Release:
                {
                    bool hasLocalPath = false;
                    bool hasAssetStoreLink = false;

                    var singleInfo = _selected.OnlyOrDefault();

                    if (singleInfo != null)
                    {
                        var info = (ReleaseInfo)singleInfo.Tag;

                        hasLocalPath = info.LocalPath != null && File.Exists(info.LocalPath);

                        hasAssetStoreLink = info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.LinkId);
                    }

                    contextMenu.AddOptionalItem(hasLocalPath, new GUIContent("Open Folder"), false, OpenReleaseFolderForSelected);

                    contextMenu.AddOptionalItem(singleInfo != null, new GUIContent("More Info..."), false, OpenMoreInfoPopupForSelected);

                    contextMenu.AddOptionalItem(hasAssetStoreLink, new GUIContent("Open In Asset Store"), false, OpenSelectedInAssetStore);
                    break;
                }
                case ListTypes.Package:
                {
                    contextMenu.AddItem(new GUIContent("Delete"), false, DeleteSelected);
                    contextMenu.AddOptionalItem(_selected.Count == 1, new GUIContent("Open Folder"), false, OpenPackageFolderForSelected);
                    break;
                }
                case ListTypes.AssetItem:
                case ListTypes.PluginItem:
                {
                    contextMenu.AddOptionalItem(_selected.Count == 1 && HasFolderWithPackageName(_selected.Single().Name), new GUIContent("Select in Project Tab"), false, ShowSelectedInProjectTab);
                    break;
                }
                default:
                {
                    Assert.Throw();
                    break;
                }
            }

            contextMenu.ShowAsContext();
        }

        bool HasFolderWithPackageName(string name)
        {
            return TryGetAssetForPackageName(name) != null;
        }

        UnityEngine.Object TryGetAssetForPackageName(string name)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/" + name);

            if (asset == null)
            {
                asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Plugins/" + name);
            }

            return asset;
        }

        void ShowSelectedInProjectTab()
        {
            Assert.That(_selected.Select(x => ClassifyList(x.ListOwner)).All(x => x == ListTypes.PluginItem || x == ListTypes.AssetItem));
            Assert.IsEqual(_selected.Count, 1);

            var name = _selected.Single().Name;

            var asset = TryGetAssetForPackageName(name);

            if (asset == null)
            {
                SetBackgroundTask(DisplayError("Could not find package '{0}' in project".Fmt(name)), false);
            }
            else
            {
                Selection.activeObject = asset;
            }
        }

        void OpenSelectedInAssetStore()
        {
            Assert.IsEqual(_selected.Count, 1);

            var entry = _selected.Single();

            Assert.IsEqual(ClassifyList(entry.ListOwner), ListTypes.Release);

            var info = (ReleaseInfo)entry.Tag;
            var assetStoreInfo = info.AssetStoreInfo;

            Assert.IsNotNull(assetStoreInfo);

            var fullUrl = "https://www.assetstore.unity3d.com/#/{0}/{1}".Fmt(assetStoreInfo.LinkType, assetStoreInfo.LinkId);
            Application.OpenURL(fullUrl);
        }

        void OpenMoreInfoPopupForSelected()
        {
            Assert.IsEqual(_selected.Count, 1);

            var entry = _selected.Single();

            switch (ClassifyList(entry.ListOwner))
            {
                case ListTypes.Package:
                {
                    SetBackgroundTask(OpenMoreInfoPopup((PackageInfo)entry.Tag), false);
                    break;
                }
                case ListTypes.Release:
                {
                    SetBackgroundTask(OpenMoreInfoPopup((ReleaseInfo)entry.Tag), false);
                    break;
                }
                default:
                {
                    Assert.Throw();
                    break;
                }
            }
        }

        IEnumerator OpenMoreInfoPopup(PackageInfo info)
        {
            Assert.Throw("TODO");
            yield break;
        }

        IEnumerator OpenMoreInfoPopup(ReleaseInfo info)
        {
            Assert.IsNull(_popupHandler);

            bool isDone = false;

            var skin = Skin.ReleaseMoreInfoDialog;
            Vector2 scrollPos = Vector2.zero;

            _popupHandler = delegate(Rect fullRect)
            {
                var popupRect = ImguiUtil.CenterRectInRect(fullRect, skin.PopupSize);

                DrawPopupCommon(fullRect, popupRect);

                var contentRect = ImguiUtil.CreateContentRectWithPadding(
                    popupRect, skin.PanelPadding);

                GUILayout.BeginArea(contentRect);
                {
                    GUILayout.Label("Release Info", skin.HeadingStyle);

                    GUILayout.Space(skin.HeadingBottomPadding);

                    scrollPos = GUILayout.BeginScrollView(scrollPos, false, true, GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar, skin.ScrollViewStyle, GUILayout.Height(skin.ListHeight));
                    {
                        GUILayout.Space(skin.ListPaddingTop);

                        DrawMoreInfoRow(skin, "Name", info.Name);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Version", string.IsNullOrEmpty(info.Version) ? NotAvailableLabel : info.Version);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Publish Date", info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.PublishDate) ? info.AssetStoreInfo.PublishDate : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Compressed Size", info.HasCompressedSize ? MiscUtil.ConvertByteSizeToDisplayValue(info.CompressedSize) : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Publisher", info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.PublisherLabel) ? info.AssetStoreInfo.PublisherLabel : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Category", info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.CategoryLabel) ? info.AssetStoreInfo.CategoryLabel : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Description", info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.Description) ? info.AssetStoreInfo.Description : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Unity Version", info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.UnityVersion) ? info.AssetStoreInfo.UnityVersion : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "ID", info.Id);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Publish Notes", info.AssetStoreInfo != null && !string.IsNullOrEmpty(info.AssetStoreInfo.PublishNotes) ? info.AssetStoreInfo.PublishNotes : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                        DrawMoreInfoRow(skin, "Version Code", info.HasVersionCode ? info.VersionCode.ToString() : NotAvailableLabel);
                        GUILayout.Space(skin.RowSpacing);
                    }
                    GUI.EndScrollView();
                }
                GUILayout.EndArea();

                var okButtonRect = new Rect(
                    contentRect.xMin + 0.5f * contentRect.width - 0.5f * skin.OkButtonWidth,
                    contentRect.yMax - skin.MarginBottom - skin.OkButtonHeight,
                    skin.OkButtonWidth,
                    skin.OkButtonHeight);

                if (GUI.Button(okButtonRect, "Ok") || Event.current.keyCode == KeyCode.Escape)
                {
                    isDone = true;
                }
            };

            while (!isDone)
            {
                yield return null;
            }

            _popupHandler = null;
        }

        void DrawMoreInfoRow(PackageManagerWindowSkin.ReleaseInfoMoreInfoDialogProperties skin, string label, string value)
        {
            GUILayout.BeginHorizontal();
            {
                if (value == NotAvailableLabel)
                {
                    GUI.color = skin.NotAvailableColor;
                }
                GUILayout.Label(label + ":", skin.LabelStyle, GUILayout.Width(skin.LabelColumnWidth));
                GUILayout.Space(skin.ColumnSpacing);
                GUILayout.Label(value, skin.ValueStyle, GUILayout.Width(skin.ValueColumnWidth));
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();
        }

        void OpenReleaseFolderForSelected()
        {
            Assert.IsEqual(_selected.Count, 1);

            var info = (ReleaseInfo)_selected.Single().Tag;

            Assert.IsNotNull(info.LocalPath);
            PathUtil.AssertPathIsValid(info.LocalPath);

            var args = @"/select, " + info.LocalPath;
            System.Diagnostics.Process.Start("explorer.exe", args);
        }

        void OpenPackageFolderForSelected()
        {
            Assert.IsEqual(_selected.Count, 1);

            var info = (PackageInfo)_selected.Single().Tag;

            Assert.That(Directory.Exists(info.Path));

            System.Diagnostics.Process.Start(info.Path);
        }

        void SelectAll()
        {
            if (_selected.IsEmpty())
            {
                return;
            }

            var listType = ClassifyList(_selected[0].ListOwner);

            Assert.That(_selected.All(x => ClassifyList(x.ListOwner) == listType));

            foreach (var entry in _selected[0].ListOwner.Values)
            {
                _selected.Add(entry);
            }
        }

        void DeleteSelected()
        {
            SetBackgroundTask(DeleteSelectedAsync(), true, "Deleting Packages");
        }

        IEnumerator DeleteSelectedAsync()
        {
            if (_selected.IsEmpty())
            {
                yield break;
            }

            var listType = ClassifyList(_selected[0].ListOwner);

            Assert.That(_selected.All(x => ClassifyList(x.ListOwner) == listType));

            if (listType == ListTypes.Package)
            {
                var infos = _selected.Select(x => (PackageInfo)x.Tag).ToList();

                var choice = PromptForUserChoice(
                    null, "<color=yellow>Are you sure you wish to delete the following packages?</color>\n\n{0}\n\n<color=yellow>Please note the following:</color>\n\nThis change is not undoable\nAny changes that you've made since installing will be lost\nAny projects or other packages that still depend on this package may be put in an invalid state by deleting it".Fmt(infos.Select(x => x.Name).Join("\n")),
                    "Delete", "Cancel");

                yield return choice;

                if (choice.Current == 0)
                {
                    yield return ProcessUpmCommand("Deleting selected packages", UpmHelper.DeletePackagesAsync(infos));
                    yield return RefreshPackagesAsync();
                }
            }
            else if (listType == ListTypes.AssetItem || listType == ListTypes.PluginItem)
            {
                var entriesToRemove = _selected.ToList();
                _selected.Clear();

                foreach (var entry in entriesToRemove)
                {
                    entry.ListOwner.Remove(entry);
                }
            }
        }

        public IEnumerator AlertUser(string title, string message)
        {
            return PromptForUserChoice(title, message, "Ok");
        }

        public IEnumerator<int> PromptForUserChoice(string title, string question, params string[] choices)
        {
            return CoRoutine.Wrap<int>(PromptForUserChoiceInternal(title, question, choices));
        }

        public IEnumerator PromptForUserChoiceInternal(
            string title, string question, params string[] choices)
        {
            Assert.IsNull(_popupHandler);

            int choice = -1;

            var skin = Skin.GenericPromptDialog;

            _popupHandler = delegate(Rect fullRect)
            {
                GUILayout.BeginArea(fullRect);
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.BeginHorizontal();
                    {
                        GUILayout.FlexibleSpace();
                        GUILayout.BeginVertical(skin.BackgroundStyle, GUILayout.Width(skin.PopupWidth));
                        {
                            GUILayout.Space(skin.PanelPadding);

                            GUILayout.BeginHorizontal();
                            {
                                GUILayout.Space(skin.PanelPadding);

                                GUILayout.BeginVertical();
                                {
                                    if (title != null)
                                    {
                                        GUILayout.Label(title, skin.TitleStyle);
                                        GUILayout.Space(skin.TitleBottomPadding);
                                    }

                                    GUILayout.Label(question, skin.LabelStyle);

                                    GUILayout.Space(skin.ButtonTopPadding);

                                    GUILayout.BeginHorizontal();
                                    {
                                        GUILayout.FlexibleSpace();

                                        for (int i = 0; i < choices.Length; i++)
                                        {
                                            if (i > 0)
                                            {
                                                GUILayout.Space(skin.ButtonSpacing);
                                            }

                                            if (GUILayout.Button(choices[i], GUILayout.Width(skin.ButtonWidth)))
                                            {
                                                choice = i;
                                            }
                                        }

                                        GUILayout.FlexibleSpace();
                                    }
                                    GUILayout.EndHorizontal();
                                }
                                GUILayout.EndVertical();
                                GUILayout.Space(skin.PanelPadding);
                            }
                            GUILayout.EndHorizontal();
                            GUILayout.Space(skin.PanelPadding);
                        }
                        GUILayout.EndVertical();
                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndArea();
            };

            while (choice == -1)
            {
                yield return null;
            }

            _popupHandler = null;

            yield return choice;
        }

        public void OnDragDrop(DraggableList.DragData data, DraggableList dropList)
        {
            if (data.SourceList == dropList || !IsDragAllowed(data, dropList))
            {
                return;
            }

            var sourceListType = ClassifyList(data.SourceList);
            var dropListType = ClassifyList(dropList);

            switch (dropListType)
            {
                case ListTypes.Package:
                {
                    switch (sourceListType)
                    {
                        case ListTypes.PluginItem:
                        case ListTypes.AssetItem:
                        {
                            foreach (var entry in data.Entries)
                            {
                                data.SourceList.Remove(entry);
                            }
                            break;
                        }
                        case ListTypes.Release:
                        {
                            SetBackgroundTask(InstallReleasesAsync(data.Entries.Select(x => (ReleaseInfo)x.Tag).ToList()), true, "Installing Releases");
                            break;
                        }
                        default:
                        {
                            Assert.Throw();
                            break;
                        }
                    }

                    break;
                }
                case ListTypes.PluginItem:
                case ListTypes.AssetItem:
                {
                    switch (sourceListType)
                    {
                        case ListTypes.AssetItem:
                        case ListTypes.PluginItem:
                        {
                            foreach (var entry in data.Entries)
                            {
                                data.SourceList.Remove(entry);
                                dropList.Add(entry.Name, entry.Tag);
                            }
                            break;
                        }
                        case ListTypes.Package:
                        {
                            foreach (var entry in data.Entries)
                            {
                                if (!dropList.DisplayValues.Contains(entry.Name))
                                {
                                    var otherList = dropListType == ListTypes.PluginItem ? _assetsList : _pluginsList;

                                    if (otherList.DisplayValues.Contains(entry.Name))
                                    {
                                        otherList.Remove(entry.Name);
                                    }

                                    dropList.Add(entry.Name);
                                }
                            }
                            break;
                        }
                        default:
                        {
                            Assert.Throw();
                            break;
                        }
                    }

                    break;
                }
                case ListTypes.Release:
                {
                    // Nothing can drag here
                    break;
                }
                default:
                {
                    Assert.Throw();
                    break;
                }
            }
        }

        IEnumerator InstallReleasesAsync(List<ReleaseInfo> releaseInfos)
        {
            // Need to make sure we have the most recent package list so we can determine whether this is
            // an upgrade / downgrade / etc.
            yield return RefreshPackagesAsync();

            foreach (var releaseInfo in releaseInfos)
            {
                var userChoice = CheckShouldInstall(releaseInfo);

                yield return userChoice;

                switch (userChoice.Current)
                {
                    case InstallReleaseUserChoices.Cancel:
                    {
                        yield break;
                    }
                    case InstallReleaseUserChoices.Install:
                    {
                        yield return ProcessUpmCommand("Installing release '{0}'".Fmt(releaseInfo.Name), UpmHelper.InstallReleaseAsync(releaseInfo));
                        break;
                    }
                    case InstallReleaseUserChoices.Skip:
                    {
                        // Do nothing
                        break;
                    }
                    default:
                    {
                        Assert.Throw();
                        break;
                    }
                }
            }

            yield return RefreshPackagesAsync();
        }

        IEnumerator<InstallReleaseUserChoices> CheckShouldInstall(ReleaseInfo releaseInfo)
        {
            return CoRoutine.Wrap<InstallReleaseUserChoices>(CheckShouldInstallInternal(releaseInfo));
        }

        IEnumerator CheckShouldInstallInternal(ReleaseInfo releaseInfo)
        {
            var packageInfo = TryFindPackageInfoForRelease(releaseInfo);

            if (packageInfo == null)
            {
                yield return InstallReleaseUserChoices.Install;
                yield break;
            }

            Assert.IsNotNull(packageInfo.InstallInfo);
            var packageReleaseInfo = packageInfo.InstallInfo.ReleaseInfo;

            Assert.IsNotNull(packageReleaseInfo);

            // TODO - how to handle?
            Assert.That(packageReleaseInfo.HasVersionCode);
            Assert.That(releaseInfo.HasVersionCode);

            IEnumerator<int> userChoice;

            if (packageReleaseInfo.VersionCode == releaseInfo.VersionCode)
            {
                Assert.IsEqual(releaseInfo.Version, packageReleaseInfo.Version);

                userChoice = PromptForUserChoice(
                    null, "Package '{0}' is already installed with the same version ('{1}').  Would you like to re-install it anyway?  Note that any local changes you've made to the package will be reverted."
                        .Fmt(packageReleaseInfo.Name, packageReleaseInfo.Version), "Overwrite", "Skip", "Cancel");
            }
            else if (releaseInfo.VersionCode > packageReleaseInfo.VersionCode)
            {
                userChoice = PromptForUserChoice(
                    null, "Package '{0}' is already installed with version '{1}'. Would you like to UPGRADE it to version '{2}'?  Note that any local changes you've made to the package will be lost."
                    .Fmt(releaseInfo.Name, packageReleaseInfo.Version, releaseInfo.Version), "Upgrade", "Skip", "Cancel");
            }
            else
            {
                Assert.That(releaseInfo.VersionCode < packageReleaseInfo.VersionCode);

                userChoice = PromptForUserChoice(
                    null, "Package '{0}' is already installed with version '{1}'. Would you like to DOWNGRADE it to version '{2}'?  Note that any local changes you've made to the package will be lost."
                    .Fmt(releaseInfo.Name, packageReleaseInfo.Version, releaseInfo.Version), "Downgrade", "Skip", "Cancel");
            }

            yield return userChoice;

            switch (userChoice.Current)
            {
                case 0:
                {
                    yield return InstallReleaseUserChoices.Install;
                    break;
                }
                case 1:
                {
                    yield return InstallReleaseUserChoices.Skip;
                    break;
                }
                case 2:
                {
                    yield return InstallReleaseUserChoices.Cancel;
                    break;
                }
                default:
                {
                    Assert.Throw();
                    break;
                }
            }
        }

        PackageInfo TryFindPackageInfoForRelease(ReleaseInfo releaseInfo)
        {
            foreach (var packageInfo in _allPackages)
            {
                if (packageInfo.InstallInfo != null && packageInfo.InstallInfo.ReleaseInfo != null && packageInfo.InstallInfo.ReleaseInfo.Id == releaseInfo.Id)
                {
                    return packageInfo;
                }
            }

            return null;
        }

        ListTypes ClassifyList(DraggableList list)
        {
            if (list == _installedList)
            {
                return ListTypes.Package;
            }

            if (list == _releasesList)
            {
                return ListTypes.Release;
            }

            if (list == _assetsList)
            {
                return ListTypes.AssetItem;
            }

            if (list == _pluginsList)
            {
                return ListTypes.PluginItem;
            }

            Assert.Throw();
            return ListTypes.AssetItem;
        }

        void DrawPackagesPane(Rect windowRect)
        {
            var startX = windowRect.xMin + _split1 * windowRect.width + Skin.ListVerticalSpacing;
            var endX = windowRect.xMin + _split2 * windowRect.width - Skin.ListVerticalSpacing;
            var startY = windowRect.yMin;
            var endY = windowRect.yMax;

            DrawPackagesPane2(Rect.MinMaxRect(startX, startY, endX, endY));
        }

        void DrawPackagesPane2(Rect rect)
        {
            var startX = rect.xMin;
            var endX = rect.xMax;
            var startY = rect.yMin;
            var endY = startY + Skin.HeaderHeight;

            GUI.Label(Rect.MinMaxRect(startX, startY, endX, endY), "Packages", Skin.HeaderTextStyle);

            startY = endY;
            endY = rect.yMax - Skin.ApplyButtonHeight - Skin.ApplyButtonTopPadding;

            _installedList.Draw(Rect.MinMaxRect(startX, startY, endX, endY));

            startY = endY + Skin.ApplyButtonTopPadding;
            endY = rect.yMax;

            var horizMiddle = 0.5f * (rect.xMax + rect.xMin);

            endX = horizMiddle - 0.5f * Skin.PackagesPane.ButtonPadding;

            if (GUI.Button(Rect.MinMaxRect(startX, startY, endX, endY), "Refresh"))
            {
                SetBackgroundTask(RefreshPackagesAsync(), true, "Refreshing Packages");
            }

            startX = endX + Skin.PackagesPane.ButtonPadding;
            endX = rect.xMax;

            if (GUI.Button(Rect.MinMaxRect(startX, startY, endX, endY), "New"))
            {
                SetBackgroundTask(CreateNewPackageAsync(), false, "Creating New Package");
            }
        }

        void DoMyWindow(int id)
        {
            ImguiUtil.DrawColoredQuad(new Rect(0, 0, 1000, 1000), Color.red);
        }

        void DrawProjectPane2(Rect rect)
        {
            var startX = rect.xMin;
            var endX = rect.xMax;
            var startY = rect.yMin;
            var endY = startY + Skin.HeaderHeight;

            GUI.Label(Rect.MinMaxRect(startX, startY, endX, endY), "Project", Skin.HeaderTextStyle);

            startY = endY;
            endY = startY + Skin.FileDropdownHeight;

            DrawFileDropdown(Rect.MinMaxRect(startX, startY, endX, endY));

            startY = endY;
            endY = startY + Skin.HeaderHeight;

            GUI.Label(Rect.MinMaxRect(startX, startY, endX, endY), "Assets Folder", Skin.HeaderTextStyle);

            startY = endY;
            endY = rect.yMax - Skin.ApplyButtonHeight - Skin.ApplyButtonTopPadding;

            DrawProjectPane3(Rect.MinMaxRect(startX, startY, endX, endY));

            startY = endY + Skin.ApplyButtonTopPadding;
            endY = rect.yMax;

            DrawButtons(Rect.MinMaxRect(startX, startY, endX, endY));
        }

        void DrawProjectPane3(Rect listRect)
        {
            var halfHeight = 0.5f * listRect.height;

            var rect1 = new Rect(listRect.x, listRect.y, listRect.width, halfHeight - 0.5f * Skin.ListHorizontalSpacing);
            var rect2 = new Rect(listRect.x, listRect.y + halfHeight + 0.5f * Skin.ListHorizontalSpacing, listRect.width, listRect.height - halfHeight - 0.5f * Skin.ListHorizontalSpacing);

            _assetsList.Draw(rect1);
            _pluginsList.Draw(rect2);

            GUI.Label(Rect.MinMaxRect(rect1.xMin, rect1.yMax, rect1.xMax, rect2.yMin), "Plugins Folder", Skin.HeaderTextStyle);
        }

        void DrawButtons(Rect rect)
        {
            var halfWidth = rect.width * 0.5f;
            var padding = 0.5f * Skin.ProjectButtonsPadding;

            if (GUI.Button(Rect.MinMaxRect(rect.x + halfWidth + padding, rect.y, rect.xMax, rect.yMax), "Apply"))
            {
                OverwriteConfig();
                SetBackgroundTask(ProcessUpmCommand("Updating directory links", UpmHelper.UpdateLinksAsync()), true, "Updating Links");
            }
        }

        IEnumerator RefreshReleasesAsync()
        {
            var response = ProcessUpmCommandForResult<List<ReleaseInfo>>("Looking up release list", UpmHelper.LookupReleaseListAsync());
            yield return response;

            _allReleases = response.Current;
            UpdateAvailableReleasesList();
        }

        IEnumerator<T> ProcessUpmCommandForResult<T>(string statusName, IEnumerator upmTask)
        {
            return CoRoutine.Wrap<T>(ProcessUpmCommand(statusName, upmTask));
        }

        IEnumerator ProcessUpmCommand(string statusName, IEnumerator upmTask)
        {
            _backgroundTaskInfo.StatusMessage = statusName;

            while (upmTask.MoveNext())
            {
                if (upmTask.Current is UpmHelperResponse)
                {
                    Assert.That(!upmTask.MoveNext());
                    break;
                }

                if (upmTask.Current != null)
                {
                    Assert.IsType<List<string>>(upmTask.Current);
                    var outputLines = (List<string>)upmTask.Current;

                    if (outputLines.Count > 0)
                    {
                        _backgroundTaskInfo.StatusMessage = outputLines.Last();
                    }
                }

                yield return null;
            }

            Assert.IsType<UpmHelperResponse>(upmTask.Current);
            var response = (UpmHelperResponse)upmTask.Current;

            if (response.Succeeded)
            {
                yield return response.Result;
            }
            else
            {
                var errorMessage = "Operation aborted!  UPM encountered errors: \n\n{0}".Fmt(response.ErrorMessage);
                Log.Error("Projeny: {0}", errorMessage);

                yield return DisplayError(errorMessage);

                ForceStopBackgroundTask();
                yield return null;
            }
        }

        IEnumerator DisplayError(string message)
        {
            return AlertUser("<color=red>Error!</color>", message);
        }

        void UpdateAvailableReleasesList()
        {
            _releasesList.Clear();

            foreach (var info in _allReleases)
            {
                _releasesList.Add(info.Name, info);
            }
        }

        void ForceStopBackgroundTask()
        {
            _backgroundTaskInfo = null;
        }

        void SetBackgroundTask(IEnumerator task, bool showProcessingLabel, string title = null)
        {
            Assert.IsNull(_backgroundTaskInfo);
            _backgroundTaskInfo = new BackgroundTaskInfo()
            {
                StatusTitle = title,
                CoRoutine = new CoRoutine(task),
                ShowProcessingLabel = showProcessingLabel
            };
        }

        IEnumerator CreateNewPackageAsync()
        {
            var userInput = PromptForInput("Enter new package name:", "Untitled");

            yield return userInput;

            if (userInput.Current == null)
            {
                // User Cancelled
                yield break;
            }

            _backgroundTaskInfo.ShowProcessingLabel = true;
            yield return ProcessUpmCommand("Creating Package '{0}'".Fmt(userInput.Current), UpmHelper.CreatePackageAsync(userInput.Current));
            yield return RefreshPackagesAsync();
        }

        void DrawPopupCommon(Rect fullRect, Rect popupRect)
        {
            ImguiUtil.DrawColoredQuad(fullRect, Skin.Theme.LoadingOverlayColor);
            ImguiUtil.DrawColoredQuad(popupRect, Skin.Theme.LoadingOverlapPopupColor);
        }

        IEnumerator<string> PromptForInput(string label, string defaultValue)
        {
            Assert.IsNull(_popupHandler);

            string userInput = defaultValue;
            InputDialogStates state = InputDialogStates.None;

            bool hasFocused = false;
            bool isFirst = true;

            _popupHandler = delegate(Rect fullRect)
            {
                if (Event.current.type == EventType.KeyDown)
                {
                    switch (Event.current.keyCode)
                    {
                        case KeyCode.Return:
                        {
                            state = InputDialogStates.Submitted;
                            break;
                        }
                        case KeyCode.Escape:
                        {
                            state = InputDialogStates.Cancelled;
                            break;
                        }
                    }
                }

                var popupRect = ImguiUtil.CenterRectInRect(fullRect, Skin.InputDialog.PopupSize);

                DrawPopupCommon(fullRect, popupRect);

                var contentRect = ImguiUtil.CreateContentRectWithPadding(
                    popupRect, Skin.InputDialog.PanelPadding);

                GUILayout.BeginArea(contentRect);
                {
                    GUILayout.Label(label, Skin.InputDialog.LabelStyle);

                    GUI.SetNextControlName("PopupTextField");
                    userInput = GUILayout.TextField(userInput, 100);
                    GUI.SetNextControlName("");

                    GUILayout.Space(5);

                    GUILayout.BeginHorizontal();
                    {
                        GUILayout.FlexibleSpace();

                        if (GUILayout.Button("Submit", GUILayout.MaxWidth(100)))
                        {
                            state = InputDialogStates.Submitted;
                        }

                        if (GUILayout.Button("Cancel", GUILayout.MaxWidth(100)))
                        {
                            state = InputDialogStates.Cancelled;
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndArea();

                if (isFirst)
                {
                    isFirst = false;
                    // Need to remove focus then regain focus on the text box for it to select the whole contents
                    GUI.FocusControl("");
                }
                else if (string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
                {
                    GUI.FocusControl("PopupTextField");
                }
            };

            while (state == InputDialogStates.None)
            {
                yield return null;
                hasFocused = true;
            }

            _popupHandler = null;

            if (state == InputDialogStates.Submitted)
            {
                yield return userInput;
            }
            else
            {
                // Just return null
            }
        }

        IEnumerator RefreshPackagesAsync()
        {
            var allPackages = ProcessUpmCommandForResult<List<PackageInfo>>("Looking up package list", UpmHelper.LookupPackagesListAsync());
            yield return allPackages;

            _allPackages = allPackages.Current;
            UpdateAvailablePackagesList();
        }

        void UpdateAvailablePackagesList()
        {
            _installedList.Clear();

            foreach (var info in _allPackages)
            {
                _installedList.Add(info.Name, info);
            }
        }

        void RefreshProject()
        {
            var configPath = GetProjectConfigPath();

            if (File.Exists(configPath))
            {
                var savedConfig = DeserializeProjectConfig(configPath);

                // Null when file is empty
                if (savedConfig == null)
                {
                    ClearProjectLists();
                }
                else
                {
                    PopulateListsFromConfig(savedConfig);
                }
            }
            else
            {
                ClearProjectLists();
            }
        }

        string GetProjectConfigPath()
        {
            var projectRootDir = Path.Combine(Application.dataPath, "../..");
            var unityProjectsDir = Path.Combine(projectRootDir, "..");

            switch (_projectConfigType)
            {
                case ProjectConfigTypes.LocalProject:
                {
                    return Path.Combine(projectRootDir, ProjenyEditorUtil.ProjectConfigFileName);
                }
                case ProjectConfigTypes.LocalProjectUser:
                {
                    return Path.Combine(projectRootDir, ProjenyEditorUtil.ProjectConfigUserFileName);
                }
                case ProjectConfigTypes.AllProjects:
                {
                    return Path.Combine(unityProjectsDir, ProjenyEditorUtil.ProjectConfigFileName);
                }
                case ProjectConfigTypes.AllProjectsUser:
                {
                    return Path.Combine(unityProjectsDir, ProjenyEditorUtil.ProjectConfigUserFileName);
                }
            }

            return null;
        }

        void ClearProjectLists()
        {
            _pluginsList.Clear();
            _assetsList.Clear();

            UpdateAvailablePackagesList();
        }

        void PopulateListsFromConfig(ProjectConfig config)
        {
            _pluginsList.Clear();
            _assetsList.Clear();

            foreach (var name in config.Packages)
            {
                _assetsList.Add(name);
            }

            foreach (var name in config.PackagesPlugins)
            {
                _pluginsList.Add(name);
            }

            UpdateAvailablePackagesList();
        }

        void OverwriteConfig()
        {
            File.WriteAllText(GetProjectConfigPath(), GetSerializedProjectConfigFromLists());
        }

        bool HasProjectConfigChanged()
        {
            var configPath = GetProjectConfigPath();

            var currentConfig = GetProjectConfigFromLists();

            if (!File.Exists(configPath))
            {
                return !currentConfig.Packages.IsEmpty() || !currentConfig.PackagesPlugins.IsEmpty();
            }

            var savedConfig = DeserializeProjectConfig(configPath);

            if (savedConfig == null)
            {
                return !currentConfig.Packages.IsEmpty() || !currentConfig.PackagesPlugins.IsEmpty();
            }

            return !Enumerable.SequenceEqual(currentConfig.Packages.OrderBy(t => t), savedConfig.Packages.OrderBy(t => t))
                || !Enumerable.SequenceEqual(currentConfig.PackagesPlugins.OrderBy(t => t), savedConfig.PackagesPlugins.OrderBy(t => t));
        }

        ProjectConfig DeserializeProjectConfig(string configPath)
        {
            return UpmSerializer.DeserializeProjectConfig(File.ReadAllText(configPath));
        }

        ProjectConfig GetProjectConfigFromLists()
        {
            var config = new ProjectConfig();

            config.Packages = _assetsList.DisplayValues.ToList();
            config.PackagesPlugins = _pluginsList.DisplayValues.ToList();

            return config;
        }

        string GetSerializedProjectConfigFromLists()
        {
            return UpmSerializer.SerializeProjectConfig(GetProjectConfigFromLists());
        }

        IEnumerator TryChangeProjectType(ProjectConfigTypes configType)
        {
            if (HasProjectConfigChanged())
            {
                var choice = PromptForUserChoice(
                    null, "Do you want to save changes to your project?", "Save", "Don't Save", "Cancel");

                yield return choice;

                switch (choice.Current)
                {
                    case 0:
                    {
                        OverwriteConfig();
                        break;
                    }
                    case 1:
                    {
                        // Do nothing
                        break;
                    }
                    case 2:
                    {
                        yield break;
                    }
                    default:
                    {
                        Assert.Throw();
                        break;
                    }
                }
            }

            _projectConfigType = configType;
            RefreshProject();
        }

        void DrawFileDropdown(Rect rect)
        {
            var dropDownRect = Rect.MinMaxRect(
                rect.xMin,
                rect.yMin,
                rect.xMax - Skin.FileButtonsPercentWidth * rect.width,
                rect.yMax);

            var displayValues = GetConfigTypesDisplayValues();
            var desiredConfigType = (ProjectConfigTypes)EditorGUI.Popup(dropDownRect, (int)_projectConfigType, displayValues, Skin.DropdownTextStyle);

            GUI.Button(dropDownRect, displayValues[(int)desiredConfigType]);

            if (desiredConfigType != _projectConfigType)
            {
                SetBackgroundTask(TryChangeProjectType(desiredConfigType), false);
            }

            GUI.DrawTexture(new Rect(dropDownRect.xMax - Skin.ArrowSize.x + Skin.ArrowOffset.x, dropDownRect.yMin + Skin.ArrowOffset.y, Skin.ArrowSize.x, Skin.ArrowSize.y), Skin.FileDropdownArrow);

            var startX = rect.xMax - Skin.FileButtonsPercentWidth * rect.width;
            var startY = rect.yMin;
            var endX = rect.xMax;
            var endY = rect.yMax;

            var buttonPadding = Skin.FileButtonsPadding;
            var buttonWidth = ((endX - startX) - 3 * buttonPadding) / 3.0f;
            var buttonHeight = endY - startY;

            startX = startX + buttonPadding;

            if (GUI.Button(new Rect(startX, startY, buttonWidth, buttonHeight), "Revert"))
            {
                RefreshProject();
            }

            startX = startX + buttonWidth + buttonPadding;

            if (GUI.Button(new Rect(startX, startY, buttonWidth, buttonHeight), "Save"))
            {
                OverwriteConfig();
            }

            startX = startX + buttonWidth + buttonPadding;

            if (GUI.Button(new Rect(startX, startY, buttonWidth, buttonHeight), "Open"))
            {
                var configPath = GetProjectConfigPath();
                InternalEditorUtility.OpenFileAtLineExternal(configPath, 1);
            }
        }

        string[] GetConfigTypesDisplayValues()
        {
            return new[]
            {
                ProjenyEditorUtil.ProjectConfigFileName,
                ProjenyEditorUtil.ProjectConfigUserFileName,
                ProjenyEditorUtil.ProjectConfigFileName + " (global)",
                ProjenyEditorUtil.ProjectConfigUserFileName + " (global)",
            };
        }

        void DrawArrowColumns(Rect fullRect)
        {
            var halfHeight = 0.5f * fullRect.height;

            var rect1 = new Rect(Skin.ListVerticalSpacing, halfHeight - 0.5f * Skin.ArrowHeight, Skin.ArrowWidth, Skin.ArrowHeight);

            if ((int)_viewState > 0)
            {
                if (GUI.Button(rect1, ""))
                {
                    _viewState = (ViewStates)((int)_viewState - 1);
                }

                if (Skin.ArrowLeftTexture != null)
                {
                    GUI.DrawTexture(new Rect(rect1.xMin + 0.5f * rect1.width - 0.5f * Skin.ArrowButtonIconWidth, rect1.yMin + 0.5f * rect1.height - 0.5f * Skin.ArrowButtonIconHeight, Skin.ArrowButtonIconWidth, Skin.ArrowButtonIconHeight), Skin.ArrowLeftTexture);
                }
            }

            var rect2 = new Rect(fullRect.xMax - Skin.ListVerticalSpacing - Skin.ArrowWidth, halfHeight - 0.5f * Skin.ArrowHeight, Skin.ArrowWidth, Skin.ArrowHeight);

            var numValues = Enum.GetValues(typeof(ViewStates)).Length;

            if ((int)_viewState < numValues-1)
            {
                if (GUI.Button(rect2, ""))
                {
                    _viewState = (ViewStates)((int)_viewState + 1);
                }

                if (Skin.ArrowRightTexture != null)
                {
                    GUI.DrawTexture(new Rect(rect2.xMin + 0.5f * rect2.width - 0.5f * Skin.ArrowButtonIconWidth, rect2.yMin + 0.5f * rect2.height - 0.5f * Skin.ArrowButtonIconHeight, Skin.ArrowButtonIconWidth, Skin.ArrowButtonIconHeight), Skin.ArrowRightTexture);
                }
            }
        }

        void DrawReleasePane(Rect windowRect)
        {
            var startX = windowRect.xMin;
            var endX = windowRect.xMin + _split1 * windowRect.width - Skin.ListVerticalSpacing;
            var startY = windowRect.yMin;
            var endY = windowRect.yMax;

            DrawReleasePane2(Rect.MinMaxRect(startX, startY, endX, endY));
        }

        void DrawReleasePane2(Rect rect)
        {
            var startX = rect.xMin;
            var endX = rect.xMax;
            var startY = rect.yMin;
            var endY = startY + Skin.HeaderHeight;

            GUI.Label(Rect.MinMaxRect(startX, startY, endX, endY), "Releases", Skin.HeaderTextStyle);

            var skin = Skin.ReleasesPane;

            startY = endY;
            endY = startY + skin.IconRowHeight;

            var iconRowRect = Rect.MinMaxRect(startX, startY, endX, endY);
            DrawSearchPane(iconRowRect);

            startY = endY;
            endY = rect.yMax - Skin.ApplyButtonHeight - Skin.ApplyButtonTopPadding;

            _releasesList.Draw(Rect.MinMaxRect(startX, startY, endX, endY));

            startY = endY + Skin.ApplyButtonTopPadding;
            endY = rect.yMax;

            if (GUI.Button(Rect.MinMaxRect(startX, startY, endX, endY), "Refresh"))
            {
                SetBackgroundTask(RefreshReleasesAsync(), true, "Refreshing Release List");
            }
        }

        void DrawSearchPane(Rect rect)
        {
            var startX = rect.xMin;
            var endX = rect.xMax;
            var startY = rect.yMin;
            var endY = rect.yMax;

            var skin = Skin.ReleasesPane;

            ImguiUtil.DrawColoredQuad(rect, skin.IconRowBackgroundColor);

            endX = rect.xMax - 2 * skin.ButtonWidth;

            var searchBarRect = Rect.MinMaxRect(startX, startY, endX, endY);
            if (searchBarRect.Contains(Event.current.mousePosition))
            {
                ImguiUtil.DrawColoredQuad(searchBarRect, skin.MouseOverBackgroundColor);
            }

            GUI.Label(new Rect(startX + skin.SearchIconOffset.x, startY + skin.SearchIconOffset.y, skin.SearchIconSize.x, skin.SearchIconSize.y), skin.SearchIcon);

            _releasesList.SearchFilter = GUI.TextField(
                searchBarRect, _releasesList.SearchFilter, skin.SearchTextStyle);

            startX = endX;
            endX = startX + skin.ButtonWidth;

            Rect buttonRect;

            buttonRect = Rect.MinMaxRect(startX, startY, endX, endY);
            if (buttonRect.Contains(Event.current.mousePosition))
            {
                ImguiUtil.DrawColoredQuad(buttonRect, skin.MouseOverBackgroundColor);

                if (Event.current.type == EventType.MouseDown)
                {
                    _releaseSortAscending = !_releaseSortAscending;
                    _releasesList.ForceSort();
                }
            }
            GUI.DrawTexture(buttonRect, _releaseSortAscending ? skin.SortDirDownIcon : skin.SortDirUpIcon);

            startX = endX;
            endX = startX + skin.ButtonWidth;

            buttonRect = Rect.MinMaxRect(startX, startY, endX, endY);
            if (buttonRect.Contains(Event.current.mousePosition))
            {
                ImguiUtil.DrawColoredQuad(buttonRect, skin.MouseOverBackgroundColor);

                if (Event.current.type == EventType.MouseDown)
                {
                    ShowReleasesSortMenu(new Vector2(buttonRect.xMin, buttonRect.yMax));
                }
            }
            GUI.DrawTexture(buttonRect, skin.SortIcon);
        }

        void ShowReleasesSortMenu(Vector2 startPos)
        {
            GenericMenu contextMenu = new GenericMenu();

            contextMenu.AddItem(
                new GUIContent("Order By Name"),
                _releasesSortMethod == ReleasesSortMethod.Name,
                () => ChangeReleaseSortMethod(ReleasesSortMethod.Name));

            contextMenu.AddItem(
                new GUIContent("Order By Size"),
                _releasesSortMethod == ReleasesSortMethod.Size,
                () => ChangeReleaseSortMethod(ReleasesSortMethod.Size));

            contextMenu.AddItem(
                new GUIContent("Order By Publish Date"),
                _releasesSortMethod == ReleasesSortMethod.PublishDate,
                () => ChangeReleaseSortMethod(ReleasesSortMethod.PublishDate));

            contextMenu.DropDown(new Rect(startPos.x, startPos.y, 0, 0));
        }

        void ChangeReleaseSortMethod(ReleasesSortMethod sortMethod)
        {
            _releasesSortMethod = sortMethod;
            _releasesList.ForceSort();
        }

        void DrawProjectPane(Rect windowRect)
        {
            var startX = windowRect.xMin + _split2 * windowRect.width + Skin.ListVerticalSpacing;
            var endX = windowRect.xMax - Skin.ListVerticalSpacing;
            var startY = windowRect.yMin;
            var endY = windowRect.yMax;

            var rect = Rect.MinMaxRect(startX, startY, endX, endY);

            DrawProjectPane2(rect);
        }

        public void OnGUI()
        {
            GUI.skin = Skin.GUISkin;

            var fullRect = new Rect(0, 0, this.position.width, this.position.height);

            if (_backgroundTaskInfo != null)
            {
                // Do not allow any input processing when running an async task
                GUI.enabled = false;
            }

            DrawArrowColumns(fullRect);

            var windowRect = Rect.MinMaxRect(
                Skin.ListVerticalSpacing + Skin.ArrowWidth,
                Skin.MarginTop,
                this.position.width - Skin.ListVerticalSpacing - Skin.ArrowWidth,
                this.position.height - Skin.MarginBottom);

            if (_split2 >= 0.1f)
            {
                DrawPackagesPane(windowRect);
            }

            if (_split2 <= 0.92f)
            {
                DrawProjectPane(windowRect);
            }

            if (_split1 >= 0.1f)
            {
                DrawReleasePane(windowRect);
            }

            CheckForKeypresses();

            GUI.enabled = true;

            if (_backgroundTaskInfo == null)
            {
                Assert.IsNull(_popupHandler);
            }
            else
            {
                if (_popupHandler != null)
                {
                    _popupHandler(fullRect);
                }
                else if (_backgroundTaskInfo.ShowProcessingLabel)
                {
                    DisplayGenericProcessingDialog(fullRect);
                }
            }
        }

        void CheckForKeypresses()
        {
            if (!GUI.enabled)
            {
                // Popup visible
                return;
            }

            var e = Event.current;

            if (e.type == EventType.ValidateCommand)
            {
                if (e.commandName == "SelectAll")
                {
                    e.Use();
                }
            }
            else if (e.type == EventType.ExecuteCommand)
            {
                if (e.commandName == "SelectAll")
                {
                    SelectAll();
                    e.Use();
                }
            }
            else if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Delete:
                    {
                        DeleteSelected();
                        break;
                    }
                }
            }
        }

        void DisplayGenericProcessingDialog(Rect fullRect)
        {
            var skin = Skin.AsyncPopupPane;
            var popupRect = ImguiUtil.CenterRectInRect(fullRect, skin.PopupSize);

            DrawPopupCommon(fullRect, popupRect);

            var contentRect = ImguiUtil.CreateContentRectWithPadding(
                popupRect, skin.PanelPadding);

            GUILayout.BeginArea(contentRect);
            {
                string title;

                if (string.IsNullOrEmpty(_backgroundTaskInfo.StatusTitle))
                {
                    title = "Processing";
                }
                else
                {
                    title = _backgroundTaskInfo.StatusTitle;
                }

                GUILayout.Label(title, skin.HeadingTextStyle, GUILayout.ExpandWidth(true));
                GUILayout.Space(skin.HeadingBottomPadding);

                string statusMessage = "";

                if (!string.IsNullOrEmpty(_backgroundTaskInfo.StatusMessage))
                {
                    statusMessage = _backgroundTaskInfo.StatusMessage;

                    int numExtraDots = (int)(Time.realtimeSinceStartup * skin.DotRepeatRate) % 4;

                    statusMessage += new String('.', numExtraDots);

                    // This is very hacky but the only way I can figure out how to keep the message a fixed length
                    // so that the text doesn't jump around as the number of dots change
                    // I tried using spaces instead of _ but that didn't work
                    statusMessage += "<color=#{0}>{1}</color>"
                        .Fmt(MiscUtil.ColorToHex(Skin.Theme.LoadingOverlapPopupColor), new String('_', 3 - numExtraDots));
                }

                GUILayout.Label(statusMessage, skin.StatusMessageTextStyle, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndArea();
        }

        public class BackgroundTaskInfo
        {
            public CoRoutine CoRoutine;
            public bool ShowProcessingLabel;

            public string ProcessName;
            public string StatusTitle;
            public string StatusMessage;
        }

        enum ReleasesSortMethod
        {
            Name,
            Size,
            PublishDate
        }

        enum ProjectConfigTypes
        {
            LocalProject,
            LocalProjectUser,
            AllProjects,
            AllProjectsUser,
        }

        enum InstallReleaseUserChoices
        {
            Cancel,
            Skip,
            Install,
        }

        enum ViewStates
        {
            ReleasesAndPackages,
            PackagesAndProject,
            Project,
        }

        enum InputDialogStates
        {
            None,
            Cancelled,
            Submitted
        }

        enum ListTypes
        {
            Package,
            Release,
            AssetItem,
            PluginItem
        }
    }
}
