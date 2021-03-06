﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Quartz;
using Quartz.Impl;

namespace LibKidsNoteForEveryone
{
    public class KidsNoteNotifierManager
    {
        private KidsNoteClient TheClient;
        private Configuration TheConfiguration;
        private FetchHistory History;
        private HashSet<ContentType> MonitoringTypes;
        private HashSet<string> ActiveScheduleNames;
        private Bot.NotifierBot TheBot;
        private DateTime LastErrorTime;

        public KidsNoteNotifierManager(HashSet<ContentType> monitoringTypes)
        {
            ActiveScheduleNames = new HashSet<string>();
            MonitoringTypes = monitoringTypes;
            TheConfiguration = GetConfiguration();

            FileLogger.Singleton.UseLogger = this.UseLogger;
            FileLogger.Singleton.WriteLog("Config path : " + SetupFilePath());

            LastErrorTime = DateTime.MinValue;

            TheBot = new Bot.NotifierBot(TheConfiguration.TelegramBotToken);
            TheBot.AdminUserChatId = this.AdminUserChatId;
            TheBot.AddSubscriber = this.AddSubscriber;
        }

        public void Startup()
        {
            var schedulerTask = StdSchedulerFactory.GetDefaultScheduler();
            schedulerTask.Wait();
            IScheduler scheduler = schedulerTask.Result;
            scheduler.Start();

            TheBot.Startup();
            if (TheConfiguration.ManagerChatId.Identifier != 0)
            {
                TheBot.SendAdminMessage(TheConfiguration.ManagerChatId, "서비스가 시작되었습니다");
            }
        }

        public void Cleanup()
        {
            var schedulerTask = StdSchedulerFactory.GetDefaultScheduler();
            schedulerTask.Wait();
            IScheduler scheduler = schedulerTask.Result;
            scheduler.Shutdown();

            if (TheConfiguration.ManagerChatId.Identifier != 0)
            {
                TheBot.SendAdminMessage(TheConfiguration.ManagerChatId, "서비스가 시작되었습니다");
            }
            TheBot.Cleanup();
        }

        private long AdminUserChatId()
        {
            TheConfiguration = GetConfiguration();
            return TheConfiguration.ManagerChatId.Identifier;
        }

        private bool AddSubscriber(long chatId)
        {
            string jsonPath = SetupFilePath();
            string jsonPathBackup = jsonPath + ".backup";
            bool success = true;
            try
            {
                string json = System.IO.File.ReadAllText(jsonPath);
                Configuration newConf = Configuration.FromJson(json);
                newConf.SubscriberIdList.Add(chatId);
                newConf.Save(jsonPathBackup);
                System.IO.File.Copy(jsonPathBackup, jsonPath, true);
                System.IO.File.Delete(jsonPathBackup);
            }
            catch (Exception)
            {
                success = false;
            }

            return success;
        }

        public void AddJob(KidsNoteScheduleParameters param)
        {
            var schedulerTask = StdSchedulerFactory.GetDefaultScheduler();
            schedulerTask.Wait();
            IScheduler scheduler = schedulerTask.Result;

            string jobName = param.ToString();

            if (ActiveScheduleNames.Contains(jobName))
            {
                return;
            }

            /*
            1.Seconds
            2.Minutes
            3.Hours
            4.Day - of - Month
            5.Month
            6.Day - of - Week
            7.Year(optional field)
            */

#if DEBUG
            DateTime scheduled = DateTime.Now;
            scheduled += new TimeSpan(0, 0, 0, 30);
#endif

            string cronFormat = "";
            if (param.Days == KidsNoteScheduleParameters.DaysType.MON_FRI)
            {
#if DEBUG
                cronFormat = String.Format("{0} {1} {2} ? * MON-FRI", scheduled.Second, scheduled.Minute, scheduled.Hour);
#else
                cronFormat = "0 0 * ? * MON-FRI";
#endif
            }
            else
            {
#if DEBUG
                cronFormat = String.Format("{0} {1} {2} * * ?", scheduled.Second, scheduled.Minute, scheduled.Hour);
#else
                cronFormat = "0 0 * * * ?";
#endif
            }


            IJobDetail job = JobBuilder.Create<KidsNoteScheduledJob>().WithIdentity(jobName, Constants.KISNOTE_SCHEDULER_GROUP_NAME).Build();
            job.JobDataMap.Put("param", param);
            job.JobDataMap.Put("owner", this);

            ITrigger trigger =
                TriggerBuilder.Create()
                    .WithIdentity(jobName, Constants.KISNOTE_SCHEDULER_GROUP_NAME)
                    .WithCronSchedule(cronFormat)
                    .ForJob(jobName, Constants.KISNOTE_SCHEDULER_GROUP_NAME)
                    .Build();

            scheduler.ScheduleJob(job, trigger);

            ActiveScheduleNames.Add(jobName);
        }

        private Dictionary<ContentType, LinkedList<KidsNoteContent>> GetNewContents()
        {
            Dictionary<ContentType, LinkedList<KidsNoteContent>> newContents
                = new Dictionary<ContentType, LinkedList<KidsNoteContent>>();

            try
            {
                foreach (var eachType in MonitoringTypes)
                {
                    UInt64 lastId = History.GetLastContentId(eachType);

                    int page = 1;
                    LinkedList<KidsNoteContent> newOnes = TheClient.DownloadContent(eachType, lastId, page);

                    // 공지사항은 너무 많고, 아이에게 크게 중요치 않으므로 다음페이지를 가져오지는 않는다.
                    while (eachType != ContentType.NOTICE && newOnes != null && newOnes.Count > 0 && newOnes.Last().Id > lastId)
                    {
                        System.Diagnostics.Trace.WriteLine("Get next page...");

                        ++page;
                        LinkedList<KidsNoteContent> nextOnes = TheClient.DownloadContent(eachType, lastId, page);

                        if (nextOnes.Count == 0)
                        {
                            break;
                        }

                        foreach (var nextOne in nextOnes)
                        {
                            newOnes.AddLast(nextOne);
                        }
                    }

                    if (newOnes == null)
                    {
                        HandleContentParseFailed(eachType);
                        continue;
                    }

                    if (newOnes.Count > 0)
                    {
                        newContents[eachType] = newOnes;
                    }
                }
            }
            catch (Exception)
            {
                newContents = null;
                HandleContentParseFailed(ContentType.UNSPECIFIED);
            }

            return newContents;
        }

        private void UpdateAndNotifyContents(Dictionary<ContentType, LinkedList<KidsNoteContent>> newContents)
        {
            try
            {
                List<Telegram.Bot.Types.ChatId> receivers = new List<Telegram.Bot.Types.ChatId>();
                if (TheConfiguration.SubscriberIdList != null)
                {
                    receivers.AddRange(TheConfiguration.SubscriberIdList);
                }

                if (TheConfiguration.ManagerChatId != (Telegram.Bot.Types.ChatId)0)
                {
                    receivers.Add(TheConfiguration.ManagerChatId);
                }

                TheBot.SendNewContents(receivers, new Bot.KidsNoteNotifyMessage(newContents));
                UpdateLastNotifiedIds(newContents);
            }
            catch (Exception)
            {
                HandleTelegramError();
            }
        }

        private void UpdateLastNotifiedIds(Dictionary<ContentType, LinkedList<KidsNoteContent>> newContents)
        {
            foreach (var each in newContents)
            {
                if (newContents.Count > 0)
                {
                    History.SetLastContentId(each.Key, each.Value.First().Id);
                }
            }

            History.Save(HistoryFilePath());
        }

        public void DoScheduledCheck()
        {
            TheConfiguration = GetConfigurationImpl(true);
            History = GetHistory();

            DateTime now = DateTime.Now;
            if (TheConfiguration.OperationHourBegin != 0 && TheConfiguration.OperationHourBegin > now.Hour)
            {
                return;
            }
            if (TheConfiguration.OperationHourEnd != 0 && TheConfiguration.OperationHourEnd < now.Hour)
            {
                return;
            }

            MakeNewClient();

            Dictionary<ContentType, LinkedList<KidsNoteContent>> newContents = GetNewContents();

            if (newContents != null && newContents.Count > 0)
            {
                UpdateAndNotifyContents(newContents);
                BackupToGoogleDrive(newContents);
            }
        }

        private void BackupToGoogleDrive(Dictionary<ContentType, LinkedList<KidsNoteContent>> newContents)
        {

        }

        private KidsNoteClient MakeNewClient()
        {
            TheClient = new KidsNoteClient();
            TheClient.GetCurrentConfiguration = this.GetConfiguration;

            return TheClient;
        }

        private string SetupFilePath()
        {
            string path = "";

            if (Fundamentals.Platform.IsRunningOnMono())
            {
                path = "config.json";
            }
            else
            {
                path = AppDomain.CurrentDomain.BaseDirectory + System.IO.Path.DirectorySeparatorChar + "config.json";
            }

            return path;
        }

        private string HistoryFilePath()
        {
            string path = "";

            if (Fundamentals.Platform.IsRunningOnMono())
            {
                return "history.json";
            }
            else
            {
                path = AppDomain.CurrentDomain.BaseDirectory + System.IO.Path.DirectorySeparatorChar + "history.json";
            }

            return path;
        }

        private Configuration GetConfiguration()
        {
            return GetConfigurationImpl(false);
        }

        private Configuration GetConfigurationImpl(bool forceReload)
        {
            string jsonFile = SetupFilePath();

            try
            {
                if (System.IO.File.Exists(jsonFile))
                {
                    if (TheConfiguration == null || forceReload)
                    {
                        string json = System.IO.File.ReadAllText(jsonFile);
                        TheConfiguration = Configuration.FromJson(json);
                    }

                    return TheConfiguration;
                }
                else
                {
                    HandleConfigurationLoadFailed();
                    return null;
                }
            }
            catch (Exception)
            {
                HandleConfigurationLoadFailed();
            }

            return null;
        }

        private FetchHistory GetHistory()
        {
            string jsonFile = HistoryFilePath();

            try
            {
                if (System.IO.File.Exists(jsonFile))
                {
                    if (History == null)
                    {
                        string json = System.IO.File.ReadAllText(jsonFile);
                        History = FetchHistory.FromJson(json);
                    }

                    return History;
                }

                History = new FetchHistory();
            }
            catch (Exception)
            {
                History = new FetchHistory();
            }

            return History;
        }

        private void HandleConfigurationLoadFailed()
        {
            NotifyError("설정 읽기에 실패하였습니다", false);
        }

        private void HandleContentParseFailed(ContentType type)
        {
            string message = "게시글 글 분석 실패 : " + type;
            NotifyError(message);
        }

        private void HandleTelegramError()
        {
            NotifyError("텔레그램 전송 에러가 발생하였습니다");
        }

        private void NotifyError(string message, bool checkLastTime = true)
        {
            if (checkLastTime)
            {
                TimeSpan elapsed = DateTime.Now - LastErrorTime;
                if (elapsed.TotalSeconds <= 60 * 60 * 8)
                {
                    return;
                }

                LastErrorTime = DateTime.Now;
            }

            if (TheConfiguration.ManagerChatId.Identifier != 0)
            {
                TheBot.SendAdminMessage(TheConfiguration.ManagerChatId, message);
            }
        }

        private bool UseLogger()
        {
            return TheConfiguration.UseLogger;
        }
    }

    public class KidsNoteScheduledJob : IJob
    {
        public Task Execute(IJobExecutionContext context)
        {
            JobDataMap dataMap = context.JobDetail.JobDataMap;
            KidsNoteScheduleParameters param = (KidsNoteScheduleParameters)dataMap["param"];
            KidsNoteNotifierManager owner = (KidsNoteNotifierManager)dataMap["owner"];

            Task task = Task.Run(() =>
            {
                owner.DoScheduledCheck();
            });

            return task;
        }
    }

}
