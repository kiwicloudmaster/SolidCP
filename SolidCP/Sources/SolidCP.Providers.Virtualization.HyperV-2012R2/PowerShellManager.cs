﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using SolidCP.Providers.HostedSolution;

namespace SolidCP.Providers.Virtualization
{
    public class PowerShellManager : IDisposable
    {
        private readonly string _remoteComputerName;
        protected static InitialSessionState session = null;
        object psLocker = new object();
        private bool isStatic = false;
        public bool IsStaticObj { get => isStatic; }

        protected Runspace RunSpace { get; set; }

        //public PowerShellManager(string remoteComputerName)
        //{
        //    _remoteComputerName = remoteComputerName;
        //    OpenRunspace();
        //}
        ///<summary>
        ///Attention! If you want use PowerShell as JOBS (Async) you must create a static object!
        ///</summary>
        public PowerShellManager(string remoteComputerName, bool isStaticObj)
        {
            isStatic = isStaticObj;
            _remoteComputerName = remoteComputerName;
            OpenRunspace();
        }

        protected void OpenRunspace()
        {
            HostedSolutionLog.LogStart("OpenRunspace");

            if (session == null)
            {
                session = InitialSessionState.CreateDefault();
                session.ImportPSModule(new[] {"Hyper-V"});
            }

            Runspace runSpace = RunspaceFactory.CreateRunspace(session);
            runSpace.Open();
            runSpace.SessionStateProxy.SetVariable("ConfirmPreference", "none");

            RunSpace = runSpace;
   
            HostedSolutionLog.LogEnd("OpenRunspace");
        }

        public void Dispose()
        {
            try
            {
                if (RunSpace != null && RunSpace.RunspaceStateInfo.State == RunspaceState.Opened)
                {
                    RunSpace.Close();
                    RunSpace = null;
                }
            }
            catch (Exception ex)
            {
                HostedSolutionLog.LogError("Runspace error", ex);
            }
        }

        public Collection<PSObject> Execute(Command cmd)
        {
            return Execute(cmd, true);
        }

        public Collection<PSObject> Execute(Command cmd, bool addComputerNameParameter)
        {
            return Execute(cmd, addComputerNameParameter, false);
        }
        public Collection<PSObject> Execute(Command cmd, bool addComputerNameParameter, bool withExceptions)
        {
            if (isStatic)
                throw new Exception("Invoke error: You can't execute this method from a static object!");

            return ExecuteInternal(cmd, addComputerNameParameter, withExceptions);
        }

        ///<summary>
        ///Not all commands support native Async work! 
        ///Please check your command manually that it has the -asJob argument!
        ///</summary>
        public Collection<PSObject> TryExecuteAsJob(Command cmd, bool addComputerNameParameter)
        {
            Collection<PSObject> results = null;
            try
            {
                cmd.Parameters.Add("asJob");
                results = ExecuteFromStaticObj(cmd, addComputerNameParameter, true);
            }
            catch(Exception ex)
            {
                //TODO: Add the packing of the command as "asJob"?
                //HostedSolutionLog.LogWarning("This command doesn't support native Async, try it in another way (asJobScript)");     
                HostedSolutionLog.LogError("TryExecuteAsJob", ex);
                throw ex;
            }
            return results;
        }
        ///<summary>
        ///Some commands can not be sent as Job (or wrap as asJob), this method is for them.
        ///For example for command Get-Job from Static object
        ///</summary>
        public Collection<PSObject> ExecuteFromStaticObj(Command cmd, bool addComputerNameParameter, bool withExceptions)
        {
            if (!isStatic)
                throw new Exception("Invoke error: You can't execute this method from a Non static object!");

            bool lockTaken = false;
            int timeoutMs = 1000 * 60 * 10; //We need to wait until another thread finishes working with Powershell (It's probably better to use lock() :))
            Collection<PSObject> results = null;
            try
            {
                System.Threading.Monitor.TryEnter(psLocker, timeoutMs, ref lockTaken);
                if (!lockTaken)   { HostedSolutionLog.LogWarning("ExecuteFromStaticObj too long"); }
                results = ExecuteInternal(cmd, addComputerNameParameter, withExceptions);
            }
            finally
            {
                if (lockTaken)
                    System.Threading.Monitor.Exit(psLocker);
            }
            return results;
        }
        private Collection<PSObject> ExecuteInternal(Command cmd, bool addComputerNameParameter, bool withExceptions)//, bool asJobScript, bool ignoreStaticCheck)
        {
            //if (!ignoreStaticCheck)
            //    if (isStatic && !asJobScript)
            //        throw new Exception("Invoke error: You can't execute this method Execute as not a Job from a static object");
            //    else if (!isStatic && asJobScript)
            //        throw new Exception("Invoke error: You can't execute this method Execute as a Job from a Non static object!");

            HostedSolutionLog.LogStart("Execute");

            List<object> errorList = new List<object>();

            HostedSolutionLog.DebugCommand(cmd);
            Collection<PSObject> results = null;

            // Add computerName parameter to command if it is remote server
            if (addComputerNameParameter)
            {
                if (!string.IsNullOrEmpty(_remoteComputerName))
                    cmd.Parameters.Add("ComputerName", _remoteComputerName);
            }

            // Create a pipeline
            Pipeline pipeLine = RunSpace.CreatePipeline();
            using (pipeLine)
            {
                // Add the command
                pipeLine.Commands.Add(cmd);
                // Execute the pipeline and save the objects returned.
                results = pipeLine.Invoke();

                // Only non-terminating errors are delivered here.
                // Terminating errors raise exceptions instead.
                // Log out any errors in the pipeline execution
                // NOTE: These errors are NOT thrown as exceptions! 
                // Be sure to check this to ensure that no errors 
                // happened while executing the command.
                if (pipeLine.Error != null && pipeLine.Error.Count > 0)
                {
                    foreach (object item in pipeLine.Error.ReadToEnd())
                    {
                        errorList.Add(item);
                        string errorMessage = string.Format("Invoke error: {0}", item);
                        HostedSolutionLog.LogWarning(errorMessage);
                    }
                }
            }
            pipeLine = null;

            if (withExceptions)
                ExceptionIfErrors(errorList);

            HostedSolutionLog.LogEnd("Execute");
            return results;
        }

        private static void ExceptionIfErrors(List<object> errors)
        {
            if (errors != null && errors.Count > 0)
                throw new Exception("Invoke error: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        #region Jobs commands
        ///<summary>
        ///You can call this method only from a static object, or you will get an exception!
        ///</summary>
        public void ClearOldJobs()
        {
            try
            {
                ExecuteFromStaticObj(new Command("Stop-Job -State Blocked", true), true, true); //first we have to stop blocked jobs
                //int hours = 3;
                Command cmd = new Command("Get-Job | " +
                    "Where-Object { (($_.State -NE 'Running') -AND ($_.State -NE 'Blocked')) -AND ($_.PSEndTime.AddHours(3) -lt (Get-Date)) } " +
                    "| Remove-Job", true);

                ExecuteFromStaticObj(cmd, true, true);
            }
            catch (Exception ex)
            {
                HostedSolutionLog.LogError("ClearOldJobs", ex);
            }
            
        }

        ///<summary>
        ///You can call this method only from a static object, or you will get an exception!
        ///</summary>
        public Collection<PSObject> GetJobResult(string jobId)
        {
            Command cmd = new Command("Receive-Job");
            if (string.IsNullOrEmpty(jobId))
                throw new NullReferenceException("jobId is null");
            cmd.Parameters.Add("Id", jobId);
            cmd.Parameters.Add("Keep");

            return ExecuteFromStaticObj(cmd, true, true);
        }

        ///<summary>
        ///You can call this method only from a static object, or you will get an exception!
        ///</summary>
        public Collection<PSObject> GetJob(string jobId)
        {
            //ClearOldJobs();
            Command cmd = new Command("Get-Job");

            if (string.IsNullOrEmpty(jobId))
                throw new NullReferenceException("jobId is null");
            cmd.Parameters.Add("Id", jobId);

            return ExecuteFromStaticObj(cmd, true, true); //only for Get-Job or different commands, that can't be packed as Job
        }

        ///<summary>
        ///You can call this method only from a static object, or you will get an exception!
        ///</summary>
        public Collection<PSObject> GetJobs()
        {
            //ClearOldJobs();
            Command cmd = new Command("Get-Job");
            return ExecuteFromStaticObj(cmd, true, true);
        }
        #endregion

        //TODO: Command wrapping as "asJob". Find the best solution, if possible.
        #region PackAsJobScript
        private string PackAsJob(string cmd)
        {
            return "Start-Job -ScriptBlock {" + cmd + "}";
        }

        private string ConvertCommandsAsScript(Collection<Command> cmds)
        {
            StringBuilder sb = new StringBuilder();
            string formatString = " {0} |";
            foreach (Command cmd in cmds)
            {
                sb.AppendFormat(formatString, ConvertCommandAsScript(cmd));
            }
            sb.Length--; //delete the last symbol - "|"            
            return sb.ToString();
        }

        private string ConvertCommandAsScript(Command cmd)
        {
            StringBuilder sb = new StringBuilder(cmd.CommandText);
            foreach (CommandParameter parameter in cmd.Parameters)
            {
                string strParameterValues = null;
                string formatString = " -{0} {1}";
                if (parameter.Value is string)
                    formatString = " -{0} '{1}'";
                else if (parameter.Value is bool)
                    formatString = " -{0} ${1}";
                else if (parameter.Value is string[])
                {
                    formatString = " -{0} @({1})";
                    strParameterValues = string.Format("'{0}'", string.Join("','", (string[])parameter.Value)); // 'elm1', 'elm2', 'elm3' and etc.
                }
                //TODO: else if () Is possible another array type?

                if (string.IsNullOrEmpty(strParameterValues))
                    sb.AppendFormat(formatString, parameter.Name, parameter.Value);
                else
                    sb.AppendFormat(formatString, parameter.Name, strParameterValues);
            }
            return sb.ToString();
        }
        #endregion
    }
}
