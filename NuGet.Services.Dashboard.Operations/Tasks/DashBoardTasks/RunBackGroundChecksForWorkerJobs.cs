﻿using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json.Linq;
using NuGetGallery.Operations.Common;
using AnglicanGeek.DbExecutor;
using System;
using System.Net;
using System.Web.Script.Serialization;
using NuGetGallery;
using NuGetGallery.Infrastructure;
using Elmah;
using NuGet.Services.Dashboard.Common;

namespace NuGetGallery.Operations.Tasks.DashBoardTasks
{
    [Command("RunBackGroundChecksForWorkerJobs", "Runs background checks for the worker jobs", AltName = "rbgc")]
    public class RunBackGroundChecksForWorkerJobs : DatabaseAndStorageTask
    {
        private const string BackupPrefix = "Backup_";
        private const string PackagesContainerName = "packages";
        private const string BackupPackagesContainerName = "backup";

        [Option("PackagesStorageAccount", AltName = "iis")]
        public CloudStorageAccount PackagesStorage { get; set; }
        public override void ExecuteCommand()
        {
            List<Tuple<string, string>> jobOutputs = new List<Tuple<string, string>>();
            jobOutputs.Add(new Tuple<string,string>("BackupDataBaseJob", CheckForBackUpDatabaseJob()));
            jobOutputs.Add(new Tuple<string, string>("CleanOnlineBackup", CheckForCleanOnlineDatabaseJob()));
            jobOutputs.Add(new Tuple<string, string>("PurgePackageStatistics", CheckForPurgePackagStatisticsJob()));
            jobOutputs.Add(new Tuple<string, string>("HandleQueuedPackageEdits", CheckForHandleQueuedPackageEditJob()));
            jobOutputs.Add(new Tuple<string, string>("BackupPackages", CheckForBackupPackagesJob()));
            JArray reportObject = ReportHelpers.GetJson(jobOutputs);
            ReportHelpers.CreateBlob(StorageAccount, "RunBackGroundChecksForWorkerJobsReport.json", "dashboard", "application/json", ReportHelpers.ToStream(reportObject));              
        }

        #region PrivateMethods
        private string CheckForBackUpDatabaseJob()
        {
            string outputMessage;
            var cstr = Util.GetMasterConnectionString(ConnectionString.ConnectionString);
            using(var connection = new SqlConnection(cstr))
            using (var db = new SqlExecutor(connection))
            {
                connection.Open();
                var lastBackupTime = Util.GetLastBackupTime(db, BackupPrefix);
                outputMessage = string.Format("Last backup time in utc as of {0} is {1}", DateTime.UtcNow, lastBackupTime);
                if (lastBackupTime >= DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(60)))
                {
                    new SendAlertMailTask
                    {
                        AlertSubject = "Work service job background check alert activated for BackupDataBase job",
                        Details = outputMessage,
                        AlertName = "Alert for BackupDatabase",
                        Component = "BackupDatabase Job"
                    }.ExecuteCommand();
                }               
            }
            return outputMessage;            
        }

        private string CheckForCleanOnlineDatabaseJob()
        {
            string outputMessage;
            var cstr = Util.GetMasterConnectionString(ConnectionString.ConnectionString);
            using (var connection = new SqlConnection(cstr))
            using (var dbExecutor = new SqlExecutor(connection))
            {
                connection.Open();
                var allBackups = dbExecutor.Query<Db>(
               "SELECT name, state FROM sys.databases WHERE name LIKE '" + BackupPrefix + "%' AND state = @state",
               new { state = Util.OnlineState });
                int onlineBackupCount = 0;
                if(allBackups == null || allBackups.Count() == 0)
                {
                    onlineBackupCount = 0;
                }
                else
                {
                    onlineBackupCount = allBackups.Count();
                }
              
                outputMessage = string.Format("No of online databases is {0}",onlineBackupCount);
                if (onlineBackupCount != 4) // this should be exactly 4 even if there is a copy/export in progress. Need to cross check that.
                {
                    new SendAlertMailTask
                    {
                        AlertSubject = "Work service job background check alert activated for CleanOnlineDatabase job",
                        Details = outputMessage,
                        AlertName = "Alert for CleanOnlineDatabase",
                        Component = "CleanOnlineDatabase Job"
                    }.ExecuteCommand();
                }               
            }
            return outputMessage;
        }

            private string CheckForPurgePackagStatisticsJob()
            {
                string outputMessage;
                using (var sqlConnection = new SqlConnection(ConnectionString.ConnectionString))
                {
                    using (var dbExecutor = new SqlExecutor(sqlConnection))
                    {
                        sqlConnection.Open();
                        //Get the count of records which are older than 7 days.
                        var oldRecordCount = dbExecutor.Query<Int32>("select count(*) from dbo.PackageStatistics where TimeStamp <= '{0}'", string.Format("0:YYYY-MM-dd HH:mm:ss",DateTime.UtcNow.AddDays(-7))).SingleOrDefault();
                        outputMessage = string.Format("No of Old stats record found online is {0}", oldRecordCount);
                        if(oldRecordCount > 0)
                        {
                            new SendAlertMailTask
                            {
                                AlertSubject = "Work service job background check alert activated for PurgePackageStatistics job",
                                Details = outputMessage,
                                AlertName = "Alert for PurgePackageStatistics",
                                Component = "PurgePackageStatistics Job"
                            }.ExecuteCommand();
                        }                      
                    }
                }
                return outputMessage;
            }

            private string CheckForHandleQueuedPackageEditJob()
            {
                string outputMessage;
                using (var sqlConnection = new SqlConnection(ConnectionString.ConnectionString))
                {
                    using (var dbExecutor = new SqlExecutor(sqlConnection))
                    {
                        sqlConnection.Open();
                        //get the edits that are pending for more than 3 hours.
                        var pendingEditCount = dbExecutor.Query<Int32>("select count(*) from dbo.PackageEdits where TimeStamp <= '{0}'", string.Format("0:YYYY-MM-dd HH:mm:ss",DateTime.UtcNow.AddHours(-3))).SingleOrDefault();
                        outputMessage = string.Format("No of Old stats record found online is {0}", pendingEditCount);
                        if (pendingEditCount > 0)
                        {
                            new SendAlertMailTask
                            {
                                AlertSubject = "Work service job background check alert activated for HandleQueuedPackageEdits job",
                                Details = outputMessage,
                                AlertName = "Alert for HandleQueuedPackageEdits",
                                Component = "HandleQueuedPackageEdits Job"
                            }.ExecuteCommand();
                        }                       
                    }
                }
                return outputMessage;
            }

           private string CheckForBackupPackagesJob()
           {
               string outputMessage;
               //no of new packages uploaded in the last 2 hours.
               int newPackageCount = 0;
               using (var sqlConnection = new SqlConnection(ConnectionString.ConnectionString))
               {
                   using (var dbExecutor = new SqlExecutor(sqlConnection))
                   {
                       sqlConnection.Open();
                       newPackageCount = dbExecutor.Query<Int32>(string.Format("SELECT Count (*) FROM [dbo].[Packages] where [Created] >= '{0}'", DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss"))).SingleOrDefault();                            
                   }
               }
                CloudBlobClient blobClient = StorageAccount.CreateCloudBlobClient();
                CloudBlobContainer container = blobClient.GetContainerReference("packages");
                CloudBlobContainer destinationContainer = blobClient.GetContainerReference("backup");
                int diff = container.ListBlobs().Count() - destinationContainer.ListBlobs().Count(); // ListBlobs() call might take quite some time. Need to check if we can set any attributes on the container regarding package count.
                outputMessage = string.Format("No of packages yet to be backed up is {0}.",diff);
               //BackupPackages job runs every 10 minutes. But a 2 hour buffer is given just in case if there is a delay in the backup process.
               if(diff > newPackageCount)
               {
                   new SendAlertMailTask
                   {
                       AlertSubject = "Work service job background check alert activated for BackupPackages job",
                       Details = outputMessage,
                       AlertName = "Alert for BackupPackages",
                       Component = "BackupPackages Job"
                   }.ExecuteCommand();
               }
               return outputMessage;
           }

        #region PrivateMethods
    }    
}