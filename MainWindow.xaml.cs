using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.ComponentModel;
using System.Threading;
using System.IO;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using DomainBuilder.Five9.CFG;
using System.Diagnostics;
using Microsoft.Win32;

namespace DomainBuilder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        WsAdminClient wsAdminClient = null;
        AuthHeaderInserter inserter = null;
        BackgroundWorker builderBackgroundWorker = null;
        
        JObject domainCfg = null;

        string operation = null;
        string usernameSuffix = null;
        string inboundDNIS = null;
        bool enableReasonCodes = false;

        limitTimeoutState[] quotas = null;
        string[] DNISList = null;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                wsAdminClient = new WsAdminClient();

                // Add our AuthHeaderInserter behavior to the client endpoint
                // this will invoke our behavior before every send so that
                // we can insert the "Authorization" HTTP header before it is sent.
                inserter = new AuthHeaderInserter();
                wsAdminClient.Endpoint.Behaviors.Add(new AuthHeaderBehavior(inserter));

                builderBackgroundWorker = new BackgroundWorker();
                builderBackgroundWorker.WorkerReportsProgress = true;
                builderBackgroundWorker.WorkerSupportsCancellation = true;
                builderBackgroundWorker.DoWork += new DoWorkEventHandler(builderBackgroundWorker_DoWork);
                builderBackgroundWorker.ProgressChanged += new ProgressChangedEventHandler(builderBackgroundWorker_ProgressChanged);
                builderBackgroundWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(builderBackgroundWorker_RunWorkerCompleted);
            }
            catch (Exception exc)
            {
                MessageBox.Show(exc.Message, "Initialization Error", MessageBoxButton.OK);
            }
        }

        void builderBackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            if (operation.ToUpper().Equals("CONNECT"))
            {
                this.connect(e);
            }
            if (operation.ToUpper().Equals("BUILD"))
            {
                this.build(e);
            }
        }

        void builderBackgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                MessageBox.Show("The build of the domain has been cancelled.", "Cancel Build", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            this.pbrProgress.Value = 0;

            if (operation.ToUpper().Equals("CONNECT"))
            {
                if (DNISList == null)
                {
                    MessageBox.Show("Unable to retrieve DNIS(s) for domain.  Please validate your credentials.", "Error", MessageBoxButton.OK);
                    return;
                }

                tbxUsernameSuffix.IsEnabled = true;

                foreach (string dnis in DNISList)
                {
                    ComboBoxItem cbi = new ComboBoxItem();
                    cbi.Content = dnis;
                    cbxInboundDNIS.Items.Add(cbi);
                }

                if (cbxInboundDNIS.Items.Count > 0)
                {
                    cbxInboundDNIS.SelectedIndex = 0;
                }

                btnBrowse.IsEnabled = true;
                cbxInboundDNIS.IsEnabled = true;
                chkEnableReasonCodes.IsEnabled = true;

                btnCancel.IsEnabled = false;
                btnBuild.IsEnabled = true;
            }
            if (operation.ToUpper().Equals("BUILD"))
            {
                btnCancel.IsEnabled = false;
                btnBuild.IsEnabled = true;
            }
            else if (operation.ToUpper().Equals("RESET"))
            {
                btnCancel.IsEnabled = false;
                btnBuild.IsEnabled = true;
            }
        }

        void builderBackgroundWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage == -1 && e.UserState != null)
            {
                this.lbxStatus.Items.Add(e.UserState);
                this.lbxStatus.ScrollIntoView(e.UserState);
                return;
            }

            this.pbrProgress.Value = e.ProgressPercentage;
        }

        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            inserter.Username = tbxUsername.Text;
            inserter.Password = pbxPassword.Password;

            operation = "CONNECT";

            builderBackgroundWorker.RunWorkerAsync();
        }

        private void btnBuild_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string filename = this.tbxConfigFilename.Text;

                if (File.Exists(filename))
                {
                    using (StreamReader reader = File.OpenText(filename))
                    {
                        domainCfg = (JObject)JToken.ReadFrom(new JsonTextReader(reader));
                    }
                }
                else
                {
                    MessageBox.Show("Unable to locate the specified domain definition file.", "Build Error", MessageBoxButton.OK);
                }

                operation = "BUILD";
                usernameSuffix = "@" + this.tbxUsernameSuffix.Text;
                ComboBoxItem cbi = (ComboBoxItem)this.cbxInboundDNIS.SelectedItem;
                inboundDNIS = (string)cbi.Content;
                enableReasonCodes = (bool)this.chkEnableReasonCodes.IsChecked;

                btnCancel.IsEnabled = true;
                btnBuild.IsEnabled = false;

                builderBackgroundWorker.RunWorkerAsync();
            }
            catch (Exception exc)
            {
                MessageBox.Show(exc.Message, "Build Error", MessageBoxButton.OK);
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            builderBackgroundWorker.CancelAsync();

            string cancel = "Cancel requested.  Please wait...";
            this.lbxStatus.Items.Add(cancel);
            this.lbxStatus.ScrollIntoView(cancel);
        }

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();

            dlg.FileName = this.tbxConfigFilename.Text;   // Default file name
            dlg.DefaultExt = ".json";                      // Default extension
            dlg.Filter = "Configuration files (.json)|*.json";
            dlg.CheckFileExists = true;
            dlg.CheckPathExists = true;
            dlg.Multiselect = false;
            dlg.ValidateNames = true;

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                this.tbxConfigFilename.Text = dlg.FileName;
            }
        }


        #region Connect
        private void connect(DoWorkEventArgs e)
        {
            try
            {
                builderBackgroundWorker.ReportProgress(-1, "Connecting...");

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Retrieving API Quota Limits---");
                    connectQuotas();
                    builderBackgroundWorker.ReportProgress(50);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Retrieving Unassigned DNIS(s)---");
                    connectDNIS();
                    builderBackgroundWorker.ReportProgress(100);
                }

                builderBackgroundWorker.ReportProgress(-1, "Connected!");
            }
            catch (Exception exc)
            {
                builderBackgroundWorker.ReportProgress(-1, exc.Message);
            }

            if (builderBackgroundWorker.CancellationPending)
            {
                e.Cancel = true;
            }
        }

        private void connectQuotas()
        {
            try
            {
                getCallCountersState req = new getCallCountersState();

                quotas = wsAdminClient.getCallCountersState(req);

                builderBackgroundWorker.ReportProgress(-1, "Quota limits retrieved successfully");
            }
            catch (Exception exc)
            {
                builderBackgroundWorker.ReportProgress(-1, exc.Message);
            }
        }

        private void connectDNIS()
        {
            try
            {
                this.CheckQuotaOperation(apiOperationType.Query);

                getDNISList req = new getDNISList();
                req.selectUnassigned = true;
                req.selectUnassignedSpecified = true;

                DNISList = wsAdminClient.getDNISList(req);

                builderBackgroundWorker.ReportProgress(-1, "Unassigned DNIS(s) retrieved successfully");
            }
            catch (Exception exc)
            {
                builderBackgroundWorker.ReportProgress(-1, exc.Message);
            }
        }

        #endregion

        #region Build Domain

        private void build(DoWorkEventArgs e)
        {
            try
            {
                builderBackgroundWorker.ReportProgress(-1, "Domain build starting...");

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Retrieving API Quota Limits---");
                    connectQuotas();
                    builderBackgroundWorker.ReportProgress(1);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building Skills---");
                    buildSkills();
                    builderBackgroundWorker.ReportProgress(10);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building Users---");
                    buildUsers();
                    builderBackgroundWorker.ReportProgress(20);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building User Profiles---");
                    buildUserProfiles();
                    builderBackgroundWorker.ReportProgress(30);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building Calling Lists---");
                    buildCallingLists();
                    builderBackgroundWorker.ReportProgress(40);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building IVR Scripts---");
                    buildIVRScripts();
                    builderBackgroundWorker.ReportProgress(50);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building Dispositions---");
                    buildDispositions();
                    builderBackgroundWorker.ReportProgress(60);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building Campaigns---");
                    buildCampaigns();
                    builderBackgroundWorker.ReportProgress(70);
                }

                if (!builderBackgroundWorker.CancellationPending)
                {
                    builderBackgroundWorker.ReportProgress(-1, "---Building VCC Configuration---");
                    buildConfiguration();
                    builderBackgroundWorker.ReportProgress(80);
                }

                builderBackgroundWorker.ReportProgress(100);

                builderBackgroundWorker.ReportProgress(-1, "Domain build complete!");
            }
            catch (Exception exc)
            {
                builderBackgroundWorker.ReportProgress(-1, exc.Message);
            }

            if (builderBackgroundWorker.CancellationPending)
            {
                e.Cancel = true;
            }
        }

        private void buildSkills()
        {
            JArray jsSkills = (JArray)domainCfg["skills"];

            foreach (JObject jsSkill in jsSkills)
            {
                try
                {
                    this.CheckQuotaOperation(apiOperationType.Modify);

                    createSkill req = new createSkill();
                    req.skillInfo = new skillInfo();
                    req.skillInfo.skill = new skill();
                    req.skillInfo.skill.name = (string)jsSkill["skillInfo"]["name"];
                    req.skillInfo.skill.description = (string)jsSkill["skillInfo"]["description"];
                    req.skillInfo.skill.routeVoiceMails = (bool)jsSkill["skillInfo"]["routeVoiceMails"];
                    req.skillInfo.skill.routeVoiceMailsSpecified = true;

                    createSkillResponse resp = wsAdminClient.createSkill(req);

                    skillInfo si = resp.@return;
                    builderBackgroundWorker.ReportProgress(-1, "Skill \"" + si.skill.name + "\" was created successfully.");
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.SkillAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }
        }

        private void buildUsers()
        {
            JArray jsUsers = (JArray)domainCfg["users"];

            foreach (JObject jsUser in jsUsers)
            {
                try
                {
                    this.CheckQuotaOperation(apiOperationType.Modify);

                    createUser req = new createUser();
                    req.userInfo = new userInfo();
                    req.userInfo.generalInfo = new userGeneralInfo();
                    req.userInfo.generalInfo.userName = (string)jsUser["userGeneralInfo"]["userName"] + usernameSuffix;
                    req.userInfo.generalInfo.password = (string)jsUser["userGeneralInfo"]["password"];
                    req.userInfo.generalInfo.firstName = (string)jsUser["userGeneralInfo"]["firstName"];
                    req.userInfo.generalInfo.lastName = (string)jsUser["userGeneralInfo"]["lastName"];
                    req.userInfo.generalInfo.EMail = (string)jsUser["userGeneralInfo"]["userName"] + usernameSuffix;
                    req.userInfo.generalInfo.canChangePassword = (bool)jsUser["userGeneralInfo"]["canChangePassword"];
                    req.userInfo.generalInfo.canChangePasswordSpecified = true;
                    req.userInfo.generalInfo.mustChangePassword = (bool)jsUser["userGeneralInfo"]["mustChangePassword"];
                    req.userInfo.generalInfo.mustChangePasswordSpecified = true;
                    req.userInfo.roles = new userRoles();
                    req.userInfo.roles.agent = new agentRole();

                    createUserResponse resp = wsAdminClient.createUser(req);

                    userInfo ui = resp.@return;
                    builderBackgroundWorker.ReportProgress(-1, "User \"" + ui.generalInfo.userName + "\" was created successfully.");
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.UserAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }

        }

        private void buildUserProfiles()
        {
            JArray jsUserProfiles = (JArray)domainCfg["userProfiles"];

            IList<string> users = domainCfg.SelectToken("users").Select(u => (string)u.SelectToken("userGeneralInfo.userName")).ToList();
            IList<string> skills = domainCfg.SelectToken("skills").Select(s => (string)s.SelectToken("skillInfo.name")).ToList();

            foreach (JObject jsUserProfile in jsUserProfiles)
            {
                try
                {
                    this.CheckQuotaOperation(apiOperationType.Modify);

                    createUserProfile req = new createUserProfile();
                    req.userProfile = new userProfile();
                    req.userProfile.name = (string)jsUserProfile["name"];
                    req.userProfile.description = (string)jsUserProfile["description"];
                    req.userProfile.users = users.ToArray();

                    // Tack on the username suffix
                    for (int i = 0; i < req.userProfile.users.Length; i++)
                    {
                        req.userProfile.users[i] += usernameSuffix;
                    }

                    req.userProfile.skills = skills.ToArray();
                    req.userProfile.roles = new userRoles();
                    req.userProfile.roles.agent = new agentRole();

                    List<agentPermission> permissions = new List<agentPermission>();
                    JArray jsPermissions = (JArray)jsUserProfile["permissions"];
                    foreach (JValue jsPermission in jsPermissions)
                    {
                        permissions.Add(createAgentPermission((agentPermissionType)Enum.Parse(typeof(agentPermissionType), (string)jsPermission), true));
                    }

                    req.userProfile.roles.agent.permissions = permissions.ToArray();

                    createUserProfileResponse resp = wsAdminClient.createUserProfile(req);

                    userProfile up = resp.@return;
                    builderBackgroundWorker.ReportProgress(-1, "User Profile \"" + up.name + "\" was created successfully.");
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.ObjectAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }
        }

        private void buildCallingLists()
        {
            JArray jsCallingLists = (JArray)domainCfg["callingLists"];

            foreach (JValue jsCallingList in jsCallingLists)
            {
                try
                {
                    this.CheckQuotaOperation(apiOperationType.Modify);

                    createList req = new createList();
                    req.listName = (string)jsCallingList;

                    createListResponse resp = wsAdminClient.createList(req);

                    builderBackgroundWorker.ReportProgress(-1, "Calling List \"" + req.listName + "\" was created successfully.");
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.ListAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }
        }

        private void buildIVRScripts()
        {
            JArray jsIVRScripts = (JArray)domainCfg["ivrScripts"];

            foreach (JObject jsIVRScript in jsIVRScripts)
            {
                try
                {
                    string ivrScript = File.ReadAllText((string)jsIVRScript["file"], Encoding.Default);

                    this.CheckQuotaOperation(apiOperationType.Modify);

                    createIVRScript req = new createIVRScript();
                    req.name = (string)jsIVRScript["name"];

                    createIVRScriptResponse resp = wsAdminClient.createIVRScript(req);

                    ivrScriptDef isd = resp.@return;

                    this.CheckQuotaOperation(apiOperationType.Modify);

                    modifyIVRScript reqModify = new modifyIVRScript();
                    reqModify.scriptDef = isd;
                    reqModify.scriptDef.description = (string)jsIVRScript["description"];
                    reqModify.scriptDef.xmlDefinition = ivrScript;

                    modifyIVRScriptResponse respModify = wsAdminClient.modifyIVRScript(reqModify);

                    builderBackgroundWorker.ReportProgress(-1, "IVR Script \"" + req.name + "\" was created successfully.");
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.ObjectAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }
        }

        private void buildDispositions()
        {
            JArray jsDispositions = (JArray)domainCfg["dispositions"];

            foreach (JObject jsDisposition in jsDispositions)
            {
                try
                {
                    this.CheckQuotaOperation(apiOperationType.Modify);

                    createDisposition req = new createDisposition();
                    req.disposition = new disposition();
                    req.disposition.name = (string)jsDisposition["name"];
                    req.disposition.description = (string)jsDisposition["description"];

                    createDispositionResponse resp = wsAdminClient.createDisposition(req);

                    builderBackgroundWorker.ReportProgress(-1, "Disposition \"" + req.disposition.name + "\" was created successfully.");
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.DispositionAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }
        }

        private void buildCampaigns()
        {
            JArray jsCampaigns = (JArray)domainCfg["campaigns"];

            foreach (JObject jsCampaign in jsCampaigns)
            {
                try
                {
                    string type = (string)jsCampaign["type"];
                    type = type.ToUpper();

                    if (type.Equals("INBOUND"))
                    {
                        this.CheckQuotaOperation(apiOperationType.Modify);

                        createInboundCampaign req = new createInboundCampaign();
                        req.campaign = new inboundCampaign();
                        req.campaign.type = (campaignType)Enum.Parse(typeof(campaignType), type);
                        req.campaign.name = (string)jsCampaign["name"];
                        req.campaign.description = (string)jsCampaign["description"];
                        req.campaign.maxNumOfLines = (int)jsCampaign["maxNumOfLines"];
                        req.campaign.defaultIvrSchedule = new ivrScriptSchedule();
                        req.campaign.defaultIvrSchedule.scriptName = (string)jsCampaign["ivrScript"];

                        createInboundCampaignResponse resp = wsAdminClient.createInboundCampaign(req);

                        this.CheckQuotaOperation(apiOperationType.Modify);

                        addDNISToCampaign reqDNIS = new addDNISToCampaign();
                        reqDNIS.campaignName = req.campaign.name;
                        reqDNIS.DNISList = inboundDNIS.Split(",".ToCharArray());

                        addDNISToCampaignResponse respDNIS = wsAdminClient.addDNISToCampaign(reqDNIS);

                        this.CheckQuotaOperation(apiOperationType.Modify);

                        addDispositionsToCampaign reqDispo = new addDispositionsToCampaign();
                        reqDispo.campaignName = req.campaign.name;
                        reqDispo.dispositions = jsCampaign["dispositions"].Select(d => (string)d).ToList().ToArray();

                        addDispositionsToCampaignResponse respDispo = wsAdminClient.addDispositionsToCampaign(reqDispo);

                        builderBackgroundWorker.ReportProgress(-1, "Campaign \"" + req.campaign.name + "\" was created successfully.");
                    }
                    else if (type.Equals("OUTBOUND"))
                    {
                        this.CheckQuotaOperation(apiOperationType.Modify);

                        createOutboundCampaign req = new createOutboundCampaign();
                        req.campaign = new outboundCampaign();
                        req.campaign.type = (campaignType)Enum.Parse(typeof(campaignType), type);
                        req.campaign.name = (string)jsCampaign["name"];
                        req.campaign.description = (string)jsCampaign["description"];
                        req.campaign.dialingMode = (campaignDialingMode)Enum.Parse(typeof(campaignDialingMode), ((string)jsCampaign["dialingMode"]).ToUpper());
                        req.campaign.dialingModeSpecified = true;
                        req.campaign.trainingMode = (bool)jsCampaign["trainingMode"];
                        req.campaign.trainingModeSpecified = true;
                        createOutboundCampaignResponse resp = wsAdminClient.createOutboundCampaign(req);

                        this.CheckQuotaOperation(apiOperationType.Modify);

                        // Add the Calling Lists to the Campaign
                        addListsToCampaign reqList = new addListsToCampaign();
                        reqList.campaignName = req.campaign.name;

                        List<listState> lists = new List<listState>();
                        foreach (JValue list in jsCampaign["lists"])
                        {
                            listState ls = new listState();
                            ls.campaignName = req.campaign.name;
                            ls.listName = (string)list;

                            lists.Add(ls);
                        }
                        reqList.lists = lists.ToArray();

                        addListsToCampaignResponse respList = wsAdminClient.addListsToCampaign(reqList);

                        this.CheckQuotaOperation(apiOperationType.Modify);

                        // Add the Skills to the Campaign
                        addSkillsToCampaign reqSkill = new addSkillsToCampaign();
                        reqSkill.campaignName = req.campaign.name;
                        reqSkill.skills = jsCampaign["skills"].Select(s => (string)s).ToList().ToArray();

                        addSkillsToCampaignResponse respSkill = wsAdminClient.addSkillsToCampaign(reqSkill);

                        this.CheckQuotaOperation(apiOperationType.Modify);

                        addDispositionsToCampaign reqDispo = new addDispositionsToCampaign();
                        reqDispo.campaignName = req.campaign.name;
                        reqDispo.dispositions = jsCampaign["dispositions"].Select(d => (string)d).ToList().ToArray();

                        addDispositionsToCampaignResponse respDispo = wsAdminClient.addDispositionsToCampaign(reqDispo);

                        builderBackgroundWorker.ReportProgress(-1, "Campaign \"" + req.campaign.name + "\" was created successfully.");
                    }
                }
                catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.CampaignAlreadyExistsException> exc)
                {
                    builderBackgroundWorker.ReportProgress(-1, exc.Message);
                }
            }

        }

        private void buildConfiguration()
        {

            try
            {
                this.CheckQuotaOperation(apiOperationType.Modify);

                modifyVCCConfiguration req = new modifyVCCConfiguration();
                req.configuration = new vccConfiguration();
                req.configuration.miscOptions = new miscVccOptions();
                req.configuration.miscOptions.enableReasonCodes = this.enableReasonCodes;
                req.configuration.miscOptions.enableReasonCodesSpecified = true;

                modifyVCCConfigurationResponse resp = wsAdminClient.modifyVCCConfiguration(req);

                builderBackgroundWorker.ReportProgress(-1, "VCC Configuration updated successfully.");
            }
            catch (System.ServiceModel.FaultException<DomainBuilder.Five9.CFG.ListAlreadyExistsException> exc)
            {
                builderBackgroundWorker.ReportProgress(-1, exc.Message);
            }
        }

        private agentPermission createAgentPermission(agentPermissionType t, bool v)
        {
            agentPermission permission = new agentPermission();
            permission.type = t;
            permission.typeSpecified = true;
            permission.value = v;

            return permission;
        }

        #endregion

        #region Quota Limits

        private void CheckQuotaOperation(apiOperationType operation)
        {
            CheckQuotaOperation(operation, 1);
        }

        private void CheckQuotaOperation(apiOperationType operation, int count)
        {
            while (!CanPerformOperation(operation))
            {
                builderBackgroundWorker.ReportProgress(-1, "API quota limits have been exceeded, waiting 1 minute for counters to reset...");

                Thread.Sleep(60000);
            }

            IncrementOperation(operation, count);
        }
        
        public bool CanPerformOperation(apiOperationType operationType)
        {
            callCounterState ccsMinute = null;
            callCounterState ccsHour = null;
            callCounterState ccsDay = null;

            foreach (limitTimeoutState lts in quotas)
            {
                foreach (callCounterState ccs in lts.callCounterStates)
                {
                    if (lts.timeout == 60 && ccs.operationType == operationType)
                    {
                        //builderBackgroundWorker.ReportProgress(-1, "Timeout [" + lts.timeout + "]: " + ccs.operationType.ToString() + " limit[" + ccs.limit + "], value[" + ccs.value + "]");
                        ccsMinute = ccs;
                    }
                    else if (lts.timeout == 3600 && ccs.operationType == operationType)
                    {
                        //builderBackgroundWorker.ReportProgress(-1, "Timeout [" + lts.timeout + "]: " + ccs.operationType.ToString() + " limit[" + ccs.limit + "], value[" + ccs.value + "]");
                        ccsHour = ccs;
                    }
                    else if (lts.timeout == 86400 && ccs.operationType == operationType)
                    {
                        //builderBackgroundWorker.ReportProgress(-1, "Timeout [" + lts.timeout + "]: " + ccs.operationType.ToString() + " limit[" + ccs.limit + "], value[" + ccs.value + "]");
                        ccsDay = ccs;
                    }
                }
            }

            if ((ccsMinute != null && ccsMinute.value < ccsMinute.limit) &&
                 (ccsHour != null && ccsHour.value < ccsHour.limit) &&
                 (ccsDay != null && ccsDay.value < ccsDay.limit))
                return true;

            return false;
        }

        public void IncrementOperation(apiOperationType operationType, int count)
        {
            foreach (limitTimeoutState lts in quotas)
            {
                foreach (callCounterState ccs in lts.callCounterStates)
                {
                    if (ccs.operationType == operationType)
                    {
                        ccs.value += count;
                    }
                }
            }
        }

        #endregion
    }
}
