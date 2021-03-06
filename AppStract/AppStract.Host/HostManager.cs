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
using AppStract.Host.Data.Application;
using AppStract.Host.Data.Settings;
using AppStract.Host.System;
using AppStract.Host.Virtualization.Process;
using AppStract.Utilities.Logging;

namespace AppStract.Host
{
  public static class HostManager
  {

    #region Variables

    private static VirtualizedProcess _process;

    #endregion

    #region Constructors

    static HostManager()
    {
      // Binding this event might cause a SecurityException.
      // This exception is not nested in a catch clause
      // because it indicates that the process is running with insufficient privileges.
      AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Occurs when the OS is querying the current process to exit.
    /// Actions taken by an <see cref="EventHandler"/> must be handled as quick as possible.
    /// </summary>
    public static event EventHandler Exiting;

    /// <summary>
    /// Initializes the <see cref="HostCore"/> 
    /// and it's <see cref="HostCore.Configuration"/> and <see cref="HostCore.Log"/>.
    /// </summary>
    public static void InitializeCore()
    {
#if DEBUG
      HostCore.Log = new ConsoleLogger();
      EasyHook.Config.Log = new EasyHookLogService();
#else
      // How to initialize the log service without configuration?
      // How to initialize the configuration without logservice?
      throw new NotImplementedException();
#endif
      HostCore.Runtime = Runtime.Load();
      HostCore.Configuration = Configuration.LoadConfiguration();
    }

    /// <summary>
    /// Starts a process from the <see cref="ApplicationData"/> loaded from the filename specified.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// A <see cref="FileNotFoundException"/> is thrown if the <paramref name="applicationDataFile"/> can not be found.
    /// </exception>
    /// <exception cref="HostException">
    /// A <see cref="HostException"/> is thrown if the process can't be started.
    /// </exception>
    /// <param name="applicationDataFile">
    /// The file to load the <see cref="ApplicationData"/> from,
    /// representing the application to start.
    /// </param>
    public static void StartProcess(string applicationDataFile)
    {
      if (!File.Exists(applicationDataFile))
        throw new FileNotFoundException("Unable to locate the virtual application's datafile.", applicationDataFile);
      var data = ApplicationData.Load(applicationDataFile);
      if (data == null)
        throw new HostException("\"" + applicationDataFile + "\""
                                + " could not be found or contains invalid data while trying"
                                + " to start a new process based on this file.");
      var workingDirectory = new ApplicationFile(Path.GetDirectoryName(applicationDataFile));
      var startInfo = new VirtualProcessStartInfo(data, workingDirectory);
      _process = VirtualizedProcess.Start(startInfo);
    }

    #endregion

    #region Private Methods

    private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
    {
      if (Exiting != null)
        Exiting(sender, e);
    }

    #endregion

    #region Private Classes

    private class EasyHookLogService : EasyHook.IEasyLog
    {

      #region IEasyLog Members

      public void Error(string message)
      {
        HostCore.Log.Error("[EasyHook] " + message);
      }

      public void Warning(string message)
      {
        HostCore.Log.Warning("[EasyHook] " + message);
      }

      public void Information(string message)
      {
        HostCore.Log.Message("[EasyHook] " + message);
      }

      #endregion

    }

    #endregion

  }
}
