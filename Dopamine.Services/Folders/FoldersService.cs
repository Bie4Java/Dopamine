﻿using Digimezzo.Utilities.Log;
using Digimezzo.Utilities.Settings;
using Dopamine.Core.Extensions;
using Dopamine.Data;
using Dopamine.Data.Entities;
using Dopamine.Data.Repositories;
using Dopamine.Services.Entities;
using Dopamine.Services.Playback;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;

namespace Dopamine.Services.Folders
{
    public class FoldersService : IFoldersService
    {
        private IFolderRepository folderRepository;
        private IPlaybackService playbackService;
        private IList<FolderViewModel> markedFolders = new List<FolderViewModel>();
        private Timer saveMarkedFoldersTimer = new Timer(2000);

        public FoldersService(IFolderRepository folderRepository, IPlaybackService playbackService)
        {
            this.folderRepository = folderRepository;
            this.playbackService = playbackService;

            this.saveMarkedFoldersTimer.Elapsed += SaveMarkedFoldersTimer_Elapsed;
        }

        public event EventHandler FoldersChanged = delegate { };

        private async Task SaveMarkedFoldersAsync()
        {
            bool isCollectionChanged = false;

            try
            {
                isCollectionChanged = this.markedFolders.Count > 0;
                await this.folderRepository.UpdateFoldersAsync(this.markedFolders.Select(x => x.Folder).ToList());
                this.markedFolders.Clear();
            }
            catch (Exception ex)
            {
                LogClient.Error("Error updating folders. Exception: {0}", ex.Message);
            }

            if (isCollectionChanged)
            {
                // Execute on Dispatcher as this will cause a refresh of the lists
                Application.Current.Dispatcher.Invoke(() => this.FoldersChanged(this, new EventArgs()));
            }
        }

        private async void SaveMarkedFoldersTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await this.SaveMarkedFoldersAsync();
        }

        public async Task MarkFolderAsync(FolderViewModel folderViewModel)
        {
            this.saveMarkedFoldersTimer.Stop();

            await Task.Run(() =>
            {
                try
                {
                    lock (this.markedFolders)
                    {
                        if (this.markedFolders.Contains(folderViewModel))
                        {
                            this.markedFolders[this.markedFolders.IndexOf(folderViewModel)].ShowInCollection = folderViewModel.ShowInCollection;
                        }
                        else
                        {
                            this.markedFolders.Add(folderViewModel);
                        }
                    }

                    this.saveMarkedFoldersTimer.Start();
                }
                catch (Exception ex)
                {
                    LogClient.Error("Error marking folder with path='{0}'. Exception: {1}", folderViewModel.Path, ex.Message);
                }
            });
        }

        public async Task<IList<FolderViewModel>> GetFoldersAsync()
        {
            IList<Folder> folders = await this.folderRepository.GetFoldersAsync();

            IList<FolderViewModel> folderViewModels = new List<FolderViewModel>();

            try
            {
                await Task.Run(() =>
                {
                    foreach (Folder folder in folders)
                    {
                        folderViewModels.Add(new FolderViewModel(folder));
                    }

                });
            }
            catch (Exception ex)
            {
                LogClient.Error("Error while getting folders. Exception: {0}", ex.Message);
            }

            return folderViewModels;
        }

        public async Task<AddFolderResult> AddFolderAsync(string path)
        {
            AddFolderResult result = await this.folderRepository.AddFolderAsync(path);

            this.FoldersChanged(this, new EventArgs());

            return result;
        }

        public async Task<RemoveFolderResult> RemoveFolderAsync(long folderId)
        {
            RemoveFolderResult result = await this.folderRepository.RemoveFolderAsync(folderId);

            this.FoldersChanged(this, new EventArgs());

            return result;
        }

        public async Task<FolderViewModel> GetSelectedFolderAsync()
        {
            IList<FolderViewModel> allFolders = await this.GetFoldersAsync();
            string savedSelectedBrowseFolderPath = SettingsClient.Get<string>("Selections", "SelectedFolder");

            if (allFolders.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrEmpty(savedSelectedBrowseFolderPath) ||
                !allFolders.Select(x => x.SafePath).Contains(savedSelectedBrowseFolderPath.ToSafePath()))
            {
                return allFolders.First();
            }

            return allFolders.Where(x => x.SafePath.Equals(savedSelectedBrowseFolderPath.ToSafePath())).FirstOrDefault();
        }

        public async Task<IList<SubfolderViewModel>> GetSubfoldersAsync(FolderViewModel selectedRootFolder, SubfolderViewModel selectedSubfolder)
        {
            // If no root folder is selected, return no subfolders.
            if (selectedRootFolder == null)
            {
                return new List<SubfolderViewModel>();
            }

            IList<SubfolderViewModel> subFolders = new List<SubfolderViewModel>();

            await Task.Run(() =>
            {
                string[] directories = null;

                if (selectedSubfolder == null)
                {
                    // If no subfolder is selected, return the subfolders of the root folder.
                    directories = Directory.GetDirectories(selectedRootFolder.Path);
                }
                else
                {
                    string subfolderPathToBrowse = selectedSubfolder.Path;

                    // If the ".." subfolder is selected, go to the parent folder.
                    if (selectedSubfolder.IsGoToParent)
                    {
                        subfolderPathToBrowse = Directory.GetParent(selectedSubfolder.Path).FullName;
                    }

                    // If we're not browing the root folder, show a folder to go up 1 level.
                    if (!subfolderPathToBrowse.ToSafePath().Equals(selectedRootFolder.SafePath))
                    {
                        subFolders.Add(new SubfolderViewModel(subfolderPathToBrowse, true));
                    }

                    // Return the subfolders of the selected subfolder
                    directories = Directory.GetDirectories(subfolderPathToBrowse);
                }

                if (directories != null)
                {
                    foreach (string directory in directories)
                    {
                        subFolders.Add(new SubfolderViewModel(directory, false));
                    }
                }
            });

            return subFolders;
        }

        public async Task SetPlayingSubFolderAsync(IEnumerable<SubfolderViewModel> subfolderViewModels)
        {
            try
            {
                if (!this.playbackService.HasCurrentTrack)
                {
                    return;
                }

                await Task.Run(() =>
                {
                    foreach (SubfolderViewModel subfolderViewModel in subfolderViewModels)
                    {
                        subfolderViewModel.IsPlaying = false;
                        subfolderViewModel.IsPaused = true;

                        if (!this.playbackService.HasCurrentTrack)
                        {
                            continue;
                        }

                        if (!subfolderViewModel.IsGoToParent &&
                        !this.playbackService.IsStopped &&
                        this.playbackService.CurrentTrack.SafePath.Contains(subfolderViewModel.SafePath))
                        {
                            subfolderViewModel.IsPlaying = true;

                            if (this.playbackService.IsPlaying)
                            {
                                subfolderViewModel.IsPaused = false;
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                LogClient.Error($"Could not set the playing subfolder. Exception: {ex.Message}");
            }
        }

        public async Task<IList<SubfolderBreadCrumbViewModel>> GetSubfolderBreadCrumbsAsync(FolderViewModel selectedRootFolder, string selectedSubfolderPath)
        {
            string parentDirectoryPath = selectedSubfolderPath;
            IList<SubfolderBreadCrumbViewModel> subfolderBreadCrumbs = new List<SubfolderBreadCrumbViewModel>();

            // Add sub folders, if applicable.
            while (!parentDirectoryPath.ToSafePath().Equals(selectedRootFolder.SafePath))
            {
                subfolderBreadCrumbs.Add(new SubfolderBreadCrumbViewModel(parentDirectoryPath));
                parentDirectoryPath = Directory.GetParent(parentDirectoryPath)?.FullName;
            }

            // Always add the root folder
            subfolderBreadCrumbs.Add(new SubfolderBreadCrumbViewModel(selectedRootFolder.Path));

            return subfolderBreadCrumbs.Reverse().ToList();
        }
    }
}
