﻿#region Copyright (C) 2009-2010 Simon Allaeys

/*
    Copyright (C) 2009-2010 Simon Allaeys
 
    This file is part of AppStract

    AppStract is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    AppStract is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with AppStract.  If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.IO;
using System.Threading;
using AppStract.Host.Data.Application;
using AppStract.Host.System.GAC;
using AppStract.Host.Virtualization.Connection;
using AppStract.Engine.Data.Connection;
using AppStract.Engine.Virtualization;
using AppStract.Utilities.Helpers;
using EasyHook;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using SystemProcess = System.Diagnostics.Process;

namespace AppStract.Host.Virtualization.Process
{
  /// <summary>
  /// Provides access to local virtualized processes.
  /// </summary>
  internal class VirtualizedProcess : IDisposable
  {

    #region Variables

    /// <summary>
    /// All data related to the application run in the current <see cref="VirtualizedProcess"/>.
    /// </summary>
    protected readonly VirtualProcessStartInfo _startInfo;
    /// <summary>
    /// Manager object for the inter-process connection.
    /// </summary>
    protected readonly ConnectionManager _connection;
    /// <summary>
    /// Manager object for all GAC related actions.
    /// </summary>
    private readonly GacManager _gacManager;
    /// <summary>
    /// The virtualized local system process.
    /// </summary>
    private SystemProcess _process;
    /// <summary>
    /// Whether the current <see cref="VirtualizedProcess"/> has been terminated.
    /// </summary>
    private bool _hasExited;
    /// <summary>
    /// Delegates to call when the process has exited.
    /// </summary>
    private ProcessExitEventHandler _exited;
    /// <summary>
    /// The object to lock when performing actions related to finalizing the process when it exited.
    /// </summary>
    private readonly object _exitEventSyncRoot;

    #endregion

    #region Events

    /// <summary>
    /// Occurs when the current process exited.
    /// </summary>
    public event ProcessExitEventHandler Exited
    {
      add { lock (_exitEventSyncRoot) _exited += value; }
      remove { lock (_exitEventSyncRoot) _exited -= value; }
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets a value whether the associated <see cref="VirtualizedProcess"/> has been terminated.
    /// </summary>
    public bool HasExited
    {
      get { return _hasExited; }
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of <see cref="VirtualizedProcess"/>,
    /// using a default <see cref="ProcessSynchronizer"/> based on the specified <see cref="VirtualProcessStartInfo"/>.
    /// </summary>
    /// <param name="startInfo">
    /// The <see cref="VirtualProcessStartInfo"/> containing the information used to start the process with.
    /// </param>
    protected VirtualizedProcess(VirtualProcessStartInfo startInfo)
      : this(startInfo, new ProcessSynchronizer(startInfo.Files.RootDirectory, startInfo.FileSystemRuleCollection,
                                                startInfo.Files.RegistryDatabase, startInfo.RegistryRuleCollection))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="VirtualizedProcess"/>,
    /// using the <see cref="ProcessSynchronizer"/> specified.
    /// </summary>
    /// <param name="startInfo">
    /// The <see cref="VirtualProcessStartInfo"/> containing the information used to start the process with.
    /// </param>
    /// <param name="processSynchronizer">
    /// The <see cref="IProcessSynchronizer"/> to use for data synchronization with the <see cref="VirtualizedProcess"/>.
    /// </param>
    protected VirtualizedProcess(VirtualProcessStartInfo startInfo, IProcessSynchronizer processSynchronizer)
    {
      _exitEventSyncRoot = new object();
      _startInfo = startInfo;
      _connection = new ConnectionManager(processSynchronizer);
      _gacManager = new GacManager(startInfo.Files.Executable.FileName,
                                   HostCore.Configuration.Application.LibsToShare);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Starts a new <see cref="VirtualizedProcess"/> from the <see cref="VirtualProcessStartInfo"/> specified.
    /// </summary>
    /// <param name="startInfo">
    /// The <see cref="VirtualProcessStartInfo"/> containing the information used to start the process with.
    /// </param>
    /// <returns>
    /// A new <see cref="VirtualizedProcess"/> component that is associated with the process resource.
    /// </returns>
    public static VirtualizedProcess Start(VirtualProcessStartInfo startInfo)
    {
      var process = new VirtualizedProcess(startInfo);
      process.Start();
      return process;
    }

    /// <summary>
    /// Immediately stops the associated process.
    /// A final synchronization cycle is not guaranteed.
    /// </summary>
    public void Kill()
    {
      _process.Kill();
    }

    #endregion

    #region Protected Methods

    /// <summary>
    /// Starts the process using the information of the class's variables.
    /// </summary>
    protected void Start()
    {
      // Initialize the underlying resources.
      _gacManager.Initialize();
      _connection.Connect();
      _hasExited = false;
      // Start the process.
      switch (_startInfo.Files.Executable.GetLibraryType())
      {
        case LibraryType.Native:
          CreateAndInject();
          break;
        case LibraryType.Managed:
          WrapAndInject();
          break;
        default:  // Should never happen!
          throw new VirtualProcessException("Unable to start a virtualization engine for " + _startInfo.Files.Executable.FileName);
      }
      HostCore.Log.Message("A virtualized process with PID {0} has been succesfully created for {1}.",
                              _process.Id, _startInfo.Files.Executable.FileName);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Creates and injects the current <see cref="VirtualizedProcess"/>,
    /// and sets the created process component to the <see cref="_process"/> variable.
    /// </summary>
    /// <exception cref="FileNotFoundException"></exception>
    private void CreateAndInject()
    {
      int processId;
      // Get the location of the library to inject
      string libraryLocation = HostCore.Configuration.Application.LibtoInject;
      if (!File.Exists(libraryLocation))
        throw new FileNotFoundException("Unable to locate the library to inject.", libraryLocation);
      RemoteHooking.CreateAndInject(
        Path.Combine(_startInfo.WorkingDirectory.FileName, _startInfo.Files.Executable.FileName),
        // Optional command line parameters for process creation
        _startInfo.Arguments,
        // ProcessCreationFlags, no conditions are set on the created process.
        0,
        // Absolute paths of the libraries to inject, we use the same one for 32bit and 64bit
        libraryLocation, libraryLocation,
        // The process ID of the newly created process
        out processId,
        // Extra parameters being passed to the injected library entry points Run() and Initialize()
        _connection.ChannelName);
      // The process has been created, set the _process variable.
      _process = SystemProcess.GetProcessById(processId, HostCore.Runtime.CurrentProcess.MachineName);
      _process.EnableRaisingEvents = true;
      _process.Exited += Process_Exited;
    }

    /// <summary>
    /// Wraps and injects a process, used for .NET applications.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// A <see cref="FileNotFoundException"/> is thrown if the executable for the wrapper process can't be found.
    /// <br />=OR=<br />
    /// A <see cref="FileNotFoundException"/> is thrown if the library to inject into the wrapper process can't be found.
    /// </exception>
    private void WrapAndInject()
    {
      // Get the location of the files needed.
      var wrapperLocation = HostCore.Configuration.Application.WrapperExecutable;
      var libraryLocation = HostCore.Configuration.Application.LibtoInject;
      if (!File.Exists(wrapperLocation))
        throw new FileNotFoundException("Unable to locate the wrapper executable.", wrapperLocation);
      if (!File.Exists(libraryLocation))
        throw new FileNotFoundException("Unable to locate the library to inject.", libraryLocation);
      // Start wrapper process.
      var startInfo = new ProcessStartInfo
                        {
                          FileName = wrapperLocation,
                          CreateNoWindow = true
                        };
      _process = SystemProcess.Start(startInfo);
      _process.EnableRaisingEvents = true;
      _process.Exited += Process_Exited;
      // Give the process time to start and thereby avoid an AccessViolationException when injecting
      //_process.WaitForInputIdle();  // Based on message-loop detection, incompatible with command line
      Thread.Sleep(500);
      // Inject wrapper.
      try
      {
        RemoteHooking.Inject(
          // The process to inject, in this case the wrapper.
          _process.Id,
          // Absolute paths of the libraries to inject, we use the same one for 32bit and 64bit
          libraryLocation, libraryLocation,
          // The name of the channel to use for IPC.
          _connection.ChannelName,
          // The location of the executable to start the wrapped process from.
          Path.Combine(_startInfo.WorkingDirectory.FileName, _startInfo.Files.Executable.FileName),
          // The arguments to pass to the main method of the executable. 
          _startInfo.Arguments);
      }
      catch (Exception e)
      {
        if (!_process.HasExited) _process.Kill();
        HostCore.Log.Critical("Injection procedure failed.", e);
        throw;
      }
      // Hide wrapper console window.
      if (!_process.HasExited)
        ProcessHelper.SetWindowState(_process.MainWindowHandle, WindowShowStyle.Hide);
    }

    /// <summary>
    /// Handles the <see cref="_process"/>.Exited event;
    /// Sets <see cref="_hasExited"/> and raises <see cref="_exited"/>.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Process_Exited(object sender, EventArgs e)
    {
      if (Thread.CurrentThread.Name == null)
        Thread.CurrentThread.Name = "Guest Finalizer";
      HostCore.Log.Message("Guest process' Exited event is called");
      if (!_process.HasExited)
        return;
      _hasExited = true;
      NativeResultCode exitCode;
      if (!ParserHelper.TryParseEnum(_process.ExitCode, out exitCode)
          || exitCode != NativeResultCode.Success)
        HostCore.Log.Error("Guest process exited with code [{0}] {1} and message: {2}",
                          _process.ExitCode, exitCode,
                          _process.StartInfo.RedirectStandardError
                            ? _process.StandardError.ReadToEnd()
                            : "null");
      RaiseExitEvent(this, _process.ExitCode);
    }

    /// <summary>
    /// Raised the <see cref="Exited"/> eventhandler.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="exitCode"></param>
    private void RaiseExitEvent(VirtualizedProcess sender, int exitCode)
    {
      lock (_exitEventSyncRoot)
        if (_exited != null)
          _exited(sender, exitCode);
    }

    #endregion

    #region IDisposable Members

    /// <summary>
    /// Releases all resources.
    /// </summary>
    public void Dispose()
    {
      _process.Dispose();
      _gacManager.Dispose();
    }

    #endregion

  }
}
