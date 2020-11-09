﻿using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Workflow;
using Microsoft.SharePoint.Client.WorkflowServices;
using SharePoint.Modernization.Scanner.Core.Results;
using SharePoint.Modernization.Scanner.Core.Workflow;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;

namespace SharePoint.Modernization.Scanner.Core.Analyzers
{
    /// <summary>
    /// Workflow analyzer
    /// </summary>
    public class WorkflowAnalyzer : BaseAnalyzer
    {
        private class SP2010WorkFlowAssociation
        {
            public string Scope { get; set; }
            public WorkflowAssociation WorkflowAssociation { get; set; }
            public List AssociatedList { get; set; }
            public ContentType AssociatedContentType { get; set; }
        }

        private static readonly string[] OOBWorkflowIDStarts = new string[]
        {
            "e43856d2-1bb4-40ef-b08b-016d89a00",    // Publishing approval | 2013
            "3bfb07cb-5c6a-4266-849b-8d6711700409", // Collect feedback - 2010
            "46c389a4-6e18-476c-aa17-289b0c79fb8f", // Collect feedback | 2013
            "77c71f43-f403-484b-bcb2-303710e00409", // Collect signatures - 2010
            "2f213931-3b93-4f81-b021-3022434a3114", // Collect signatures | 
            "8ad4d8f0-93a7-4941-9657-cf3706f00409", // Approval - 2010
            "b4154df4-cc53-4c4f-adef-1ecf0b7417f6", // Translation management
            "c6964bff-bf8d-41ac-ad5e-b61ec111731a", // Three state | 2010
            "c6964bff-bf8d-41ac-ad5e-b61ec111731c", // Approval |
            "dd19a800-37c1-43c0-816d-f8eb5f4a4145", // Disposition approval |
        };

        public static Dictionary<string, string> OOBWorkflowNames = new Dictionary<string, string>
        {
           { "e43856d2-1bb4-40ef-b08b-016d89a00",    "Publishing approval" },
           { "3bfb07cb-5c6a-4266-849b-8d6711700409", "Collect feedback - 2010" },
           { "46c389a4-6e18-476c-aa17-289b0c79fb8f", "Collect feedback" },
           { "77c71f43-f403-484b-bcb2-303710e00409", "Collect signatures - 2010" },
           { "2f213931-3b93-4f81-b021-3022434a3114", "Collect signatures" },
           { "8ad4d8f0-93a7-4941-9657-cf3706f00409", "Approval - 2010" },
           { "b4154df4-cc53-4c4f-adef-1ecf0b7417f6", "Translation management" },
           { "c6964bff-bf8d-41ac-ad5e-b61ec111731a", "Three state" },
           { "c6964bff-bf8d-41ac-ad5e-b61ec111731c", "Approval" },
           { "dd19a800-37c1-43c0-816d-f8eb5f4a4145", "Disposition approval" }
        };

        private List<SP2010WorkFlowAssociation> sp2010WorkflowAssociations;
        private List workflowList;
        private List wfpubList;

        #region Construction
        /// <summary>
        /// Workflow analyzer construction
        /// </summary>
        /// <param name="url">Url of the web to be analyzed</param>
        /// <param name="siteColUrl">Url of the site collection hosting this web</param>
        /// <param name="scanJob">Job that launched this analyzer</param>
        public WorkflowAnalyzer(string url, string siteColUrl, ModernizationScanJob scanJob) : base(url, siteColUrl, scanJob)
        {
            this.sp2010WorkflowAssociations = new List<SP2010WorkFlowAssociation>(20);
        }
        #endregion

        #region Analysis
        /// <summary>
        /// Analyses a web for it's workflow usage
        /// </summary>
        /// <param name="cc">ClientContext instance used to retrieve workflow data</param>
        /// <returns>Duration of the workflow analysis</returns>
        public override TimeSpan Analyze(ClientContext cc)
        {
            try
            {
                // Workflow analysis does not work as the xoml / xaml files can't be read with Sites.Read.All permission
                if (!this.ScanJob.AppOnlyHasFullControl)
                {
                    return TimeSpan.Zero;
                }

                Web web = cc.Web;

                // Pre-load needed properties in a single call
                cc.Load(web, _web => _web.Id, _web => _web.Language, _web => _web.ServerRelativeUrl, _web => _web.Url, _web => _web.WorkflowTemplates, _web => _web.WorkflowAssociations, _web => _web.LastItemModifiedDate, _web => _web.LastItemUserModifiedDate);
                cc.Load(web, p => p.ContentTypes.Include(ct => ct.WorkflowAssociations, ct => ct.Name, ct => ct.StringId));
                cc.Load(web, _web => _web.Lists.Include(list => list.Id, list => list.LastItemUserModifiedDate, list => list.LastItemModifiedDate, list => list.Title, list => list.Hidden, li => li.DefaultViewUrl, li => li.BaseTemplate, li => li.RootFolder.ServerRelativeUrl, li => li.ItemCount, li => li.WorkflowAssociations, li => li.ContentTypesEnabled, li => li.ContentTypes.Include(ct => ct.WorkflowAssociations, ct => ct.Name, ct => ct.StringId)));
                cc.Load(cc.Site, p => p.RootWeb);
                cc.Load(cc.Site.RootWeb, p => p.Lists.Include(li => li.Id, li => li.Title, li => li.Hidden, li => li.DefaultViewUrl, li => li.BaseTemplate, li => li.RootFolder.ServerRelativeUrl, li => li.ItemCount, li => li.WorkflowAssociations, li => li.ContentTypesEnabled, li => li.ContentTypes.Include(ct => ct.WorkflowAssociations, ct => ct.Name, ct => ct.StringId)));
                cc.ExecuteQueryRetry();

                var lists = web.Lists;

                #region 2013 workflow

                // *******************************************
                // Site, reusable and list level 2013 workflow
                // *******************************************

                // Retrieve the 2013 site level workflow definitions (including unpublished ones)
                WorkflowDefinition[] siteDefinitions = null;
                // Retrieve the 2013 site level workflow subscriptions
                WorkflowSubscription[] siteSubscriptions = null;

                try
                {
                    var servicesManager = new WorkflowServicesManager(web.Context, web);
                    var deploymentService = servicesManager.GetWorkflowDeploymentService();
                    var subscriptionService = servicesManager.GetWorkflowSubscriptionService();

                    var definitions = deploymentService.EnumerateDefinitions(false);
                    web.Context.Load(definitions);

                    var subscriptions = subscriptionService.EnumerateSubscriptions();
                    web.Context.Load(subscriptions);

                    web.Context.ExecuteQueryRetry();

                    siteDefinitions = definitions.ToArray();
                    siteSubscriptions = subscriptions.ToArray();
                }
                catch (ServerException ex)
                {
                    // If there is no workflow service present in the farm this method will throw an error. 
                    // Swallow the exception
                }

                // We've found SP2013 site scoped workflows
                if (siteDefinitions != null && siteDefinitions.Count() > 0)
                {
                    foreach (var siteDefinition in siteDefinitions.Where(p => p.RestrictToType != null && (p.RestrictToType.Equals("site", StringComparison.InvariantCultureIgnoreCase) || p.RestrictToType.Equals("universal", StringComparison.InvariantCultureIgnoreCase))))
                    {
                        // Check if this workflow is also in use
                        var siteWorkflowSubscriptions = siteSubscriptions.Where(p => p.DefinitionId.Equals(siteDefinition.Id));

                        // Perform workflow analysis
                        WorkflowActionAnalysis workFlowAnalysisResult = null;
                        WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                        if (Options.IncludeWorkflowWithDetails(this.ScanJob.Mode))
                        {
                            workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(siteDefinition.Xaml, WorkflowTypes.SP2013);
                            workFlowTriggerAnalysisResult = WorkflowManager.Instance.ParseWorkflowTriggers(GetWorkflowPropertyBool(siteDefinition.Properties, "SPDConfig.StartOnCreate"), GetWorkflowPropertyBool(siteDefinition.Properties, "SPDConfig.StartOnChange"), GetWorkflowPropertyBool(siteDefinition.Properties, "SPDConfig.StartManually"));
                        }

                        if (siteWorkflowSubscriptions.Count() > 0)
                        {
                            foreach (var siteWorkflowSubscription in siteWorkflowSubscriptions)
                            {
                                WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                                {
                                    SiteColUrl = this.SiteCollectionUrl,
                                    SiteURL = this.SiteUrl,
                                    ListTitle = "",
                                    ListUrl = "",
                                    ContentTypeId = "",
                                    ContentTypeName = "",
                                    Version = "2013",
                                    Scope = "Site",
                                    RestrictToType = siteDefinition.RestrictToType,
                                    DefinitionName = siteDefinition.DisplayName,
                                    DefinitionDescription = siteDefinition.Description,
                                    SubscriptionName = siteWorkflowSubscription.Name,
                                    HasSubscriptions = true,
                                    Enabled = siteWorkflowSubscription.Enabled,
                                    DefinitionId = siteDefinition.Id,
                                    IsOOBWorkflow = false,
                                    SubscriptionId = siteWorkflowSubscription.Id,
                                    UsedActions = workFlowAnalysisResult?.WorkflowActions,
                                    ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                    UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                                    UnsupportedActionsInFlow = workFlowAnalysisResult?.UnsupportedActions,
                                    UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                    LastDefinitionEdit = GetWorkflowPropertyDateTime(siteDefinition.Properties, "Definition.ModifiedDateUTC"),
                                    LastSubscriptionEdit = GetWorkflowPropertyDateTime(siteWorkflowSubscription.PropertyDefinitions, "SharePointWorkflowContext.Subscription.ModifiedDateUTC"),
                                };

                                if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                                {
                                    ScanError error = new ScanError()
                                    {
                                        Error = $"Could not add 2013 site workflow scan result for {workflowScanResult.SiteColUrl}",
                                        SiteColUrl = this.SiteCollectionUrl,
                                        SiteURL = this.SiteUrl,
                                        Field1 = "WorkflowAnalyzer",
                                    };
                                    this.ScanJob.ScanErrors.Push(error);
                                }
                            }
                        }
                        else
                        {
                            WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                            {
                                SiteColUrl = this.SiteCollectionUrl,
                                SiteURL = this.SiteUrl,
                                ListTitle = "",
                                ListUrl = "",
                                ContentTypeId = "",
                                ContentTypeName = "",
                                Version = "2013",
                                Scope = "Site",
                                RestrictToType = siteDefinition.RestrictToType,
                                DefinitionName = siteDefinition.DisplayName,
                                DefinitionDescription = siteDefinition.Description,
                                SubscriptionName = "",
                                HasSubscriptions = false,
                                Enabled = false,
                                DefinitionId = siteDefinition.Id,
                                IsOOBWorkflow = false,
                                SubscriptionId = Guid.Empty,
                                UsedActions = workFlowAnalysisResult?.WorkflowActions,
                                ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                UnsupportedActionsInFlow = workFlowAnalysisResult?.UnsupportedActions,
                                UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                                LastDefinitionEdit = GetWorkflowPropertyDateTime(siteDefinition.Properties, "Definition.ModifiedDateUTC"),
                            };

                            if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                            {
                                ScanError error = new ScanError()
                                {
                                    Error = $"Could not add 2013 site workflow scan result for {workflowScanResult.SiteColUrl}",
                                    SiteColUrl = this.SiteCollectionUrl,
                                    SiteURL = this.SiteUrl,
                                    Field1 = "WorkflowAnalyzer",
                                };
                                this.ScanJob.ScanErrors.Push(error);
                            }
                        }
                    }
                }

                // We've found SP2013 list scoped workflows
                if (siteDefinitions != null && siteDefinitions.Count() > 0)
                {
                    foreach (var listDefinition in siteDefinitions.Where(p => p.RestrictToType != null && (p.RestrictToType.Equals("list", StringComparison.InvariantCultureIgnoreCase) || p.RestrictToType.Equals("universal", StringComparison.InvariantCultureIgnoreCase))))
                    {
                        // Check if this workflow is also in use
                        var listWorkflowSubscriptions = siteSubscriptions.Where(p => p.DefinitionId.Equals(listDefinition.Id));

                        // Perform workflow analysis
                        WorkflowActionAnalysis workFlowAnalysisResult = null;
                        WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                        if (Options.IncludeWorkflowWithDetails(this.ScanJob.Mode))
                        {
                            workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(listDefinition.Xaml, WorkflowTypes.SP2013);
                            workFlowTriggerAnalysisResult = WorkflowManager.Instance.ParseWorkflowTriggers(GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartOnCreate"), GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartOnChange"), GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartManually"));
                        }

                        if (listWorkflowSubscriptions.Count() > 0)
                        {
                            foreach (var listWorkflowSubscription in listWorkflowSubscriptions)
                            {
                                Guid associatedListId = Guid.Empty;
                                string associatedListTitle = "";
                                string associatedListUrl = "";
                                if (Guid.TryParse(GetWorkflowProperty(listWorkflowSubscription, "Microsoft.SharePoint.ActivationProperties.ListId"), out Guid associatedListIdValue))
                                {
                                    associatedListId = associatedListIdValue;

                                    // Lookup this list and update title and url
                                    var listLookup = lists.Where(p => p.Id.Equals(associatedListId)).FirstOrDefault();
                                    if (listLookup != null)
                                    {
                                        associatedListTitle = listLookup.Title;
                                        associatedListUrl = listLookup.RootFolder.ServerRelativeUrl;
                                    }
                                }

                                WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                                {
                                    SiteColUrl = this.SiteCollectionUrl,
                                    SiteURL = this.SiteUrl,
                                    ListTitle = associatedListTitle,
                                    ListUrl = associatedListUrl,
                                    ListId = associatedListId,
                                    ContentTypeId = "",
                                    ContentTypeName = "",
                                    Version = "2013",
                                    Scope = "List",
                                    RestrictToType = listDefinition.RestrictToType,
                                    DefinitionName = listDefinition.DisplayName,
                                    DefinitionDescription = listDefinition.Description,
                                    SubscriptionName = listWorkflowSubscription.Name,
                                    HasSubscriptions = true,
                                    Enabled = listWorkflowSubscription.Enabled,
                                    DefinitionId = listDefinition.Id,
                                    IsOOBWorkflow = false,
                                    SubscriptionId = listWorkflowSubscription.Id,
                                    UsedActions = workFlowAnalysisResult?.WorkflowActions,
                                    ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                    UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                                    UnsupportedActionsInFlow = workFlowAnalysisResult?.UnsupportedActions,
                                    UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                    LastDefinitionEdit = GetWorkflowPropertyDateTime(listDefinition.Properties, "Definition.ModifiedDateUTC"),
                                    LastSubscriptionEdit = GetWorkflowPropertyDateTime(listWorkflowSubscription.PropertyDefinitions, "SharePointWorkflowContext.Subscription.ModifiedDateUTC"),
                                };

                                if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                                {
                                    ScanError error = new ScanError()
                                    {
                                        Error = $"Could not add 2013 list workflow scan result for {workflowScanResult.SiteColUrl}",
                                        SiteColUrl = this.SiteCollectionUrl,
                                        SiteURL = this.SiteUrl,
                                        Field1 = "WorkflowAnalyzer",
                                    };
                                    this.ScanJob.ScanErrors.Push(error);
                                }
                            }
                        }
                        else
                        {
                            WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                            {
                                SiteColUrl = this.SiteCollectionUrl,
                                SiteURL = this.SiteUrl,
                                ListTitle = "",
                                ListUrl = "",
                                ListId = Guid.Empty,
                                ContentTypeId = "",
                                ContentTypeName = "",
                                Version = "2013",
                                Scope = "List",
                                RestrictToType = listDefinition.RestrictToType,
                                DefinitionName = listDefinition.DisplayName,
                                DefinitionDescription = listDefinition.Description,
                                SubscriptionName = "",
                                HasSubscriptions = false,
                                Enabled = false,
                                DefinitionId = listDefinition.Id,
                                IsOOBWorkflow = false,
                                SubscriptionId = Guid.Empty,
                                UsedActions = workFlowAnalysisResult?.WorkflowActions,
                                ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                                UnsupportedActionsInFlow = workFlowAnalysisResult?.UnsupportedActions,
                                UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                LastDefinitionEdit = GetWorkflowPropertyDateTime(listDefinition.Properties, "Definition.ModifiedDateUTC"),
                            };

                            if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                            {
                                ScanError error = new ScanError()
                                {
                                    Error = $"Could not add 2013 list workflow scan result for {workflowScanResult.SiteColUrl}",
                                    SiteColUrl = this.SiteCollectionUrl,
                                    SiteURL = this.SiteUrl,
                                    Field1 = "WorkflowAnalyzer",
                                };
                                this.ScanJob.ScanErrors.Push(error);
                            }
                        }
                    }
                }

                #endregion

                #region 2010 workflow

                // ***********************************************
                // Site, list and content type level 2010 workflow
                // ***********************************************

                // Find all places where we have workflows associated (=subscribed) to SharePoint objects
                if (web.WorkflowAssociations.Count > 0)
                {
                    foreach (var workflowAssociation in web.WorkflowAssociations)
                    {
                        this.sp2010WorkflowAssociations.Add(new SP2010WorkFlowAssociation() { Scope = "Site", WorkflowAssociation = workflowAssociation });
                    }
                }

                foreach (var list in lists.Where(p => p.WorkflowAssociations.Count > 0))
                {
                    foreach (var workflowAssociation in list.WorkflowAssociations)
                    {
                        this.sp2010WorkflowAssociations.Add(new SP2010WorkFlowAssociation() { Scope = "List", WorkflowAssociation = workflowAssociation, AssociatedList = list });
                    }
                }

                foreach (var list in lists.Where(p => p.ContentTypesEnabled))
                {
                    foreach (var listContentType in list.ContentTypes.Where(p => p.WorkflowAssociations.Count > 0))
                    {
                        foreach (var workflowAssociation in listContentType.WorkflowAssociations)
                        {
                            this.sp2010WorkflowAssociations.Add(new SP2010WorkFlowAssociation() { Scope = "ContentType", WorkflowAssociation = workflowAssociation, AssociatedContentType = listContentType, AssociatedList = list });
                        }
                    }
                }

                foreach (var ct in web.ContentTypes.Where(p => p.WorkflowAssociations.Count > 0))
                {
                    foreach (var workflowAssociation in ct.WorkflowAssociations)
                    {
                        this.sp2010WorkflowAssociations.Add(new SP2010WorkFlowAssociation() { Scope = "ContentType", WorkflowAssociation = workflowAssociation, AssociatedContentType = ct });
                    }
                }

                // Process 2010 worflows
                List<Guid> processedWorkflowAssociations = new List<Guid>(this.sp2010WorkflowAssociations.Count);

                if (web.WorkflowTemplates.Count > 0)
                {
                    // Process the templates
                    foreach (var workflowTemplate in web.WorkflowTemplates)
                    {
                        // do we have workflows associated for this template?
                        var associatedWorkflows = this.sp2010WorkflowAssociations.Where(p => p.WorkflowAssociation.BaseId.Equals(workflowTemplate.Id));
                        if (associatedWorkflows.Count() > 0)
                        {
                            // Perform workflow analysis
                            // If returning null than this workflow template was an OOB workflow one
                            WorkflowActionAnalysis workFlowAnalysisResult = null;
                            Tuple<string, DateTime> loadedWorkflow = null;

                            if (Options.IncludeWorkflowWithDetails(this.ScanJob.Mode))
                            {
                                loadedWorkflow = LoadWorkflowDefinition(cc, workflowTemplate);
                                if (!string.IsNullOrEmpty(loadedWorkflow?.Item1))
                                {
                                    workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(loadedWorkflow.Item1, WorkflowTypes.SP2010);
                                }
                            }

                            foreach (var associatedWorkflow in associatedWorkflows)
                            {
                                processedWorkflowAssociations.Add(associatedWorkflow.WorkflowAssociation.Id);

                                // Skip previous versions of a workflow
                                // TODO: non-english sites will use another string
                                if (associatedWorkflow.WorkflowAssociation.Name.Contains(PreviousWorkflowString(web.Language)))
                                {
                                    continue;
                                }

                                WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                                if (Options.IncludeWorkflowWithDetails(this.ScanJob.Mode))
                                {
                                    workFlowTriggerAnalysisResult = WorkflowManager.Instance.ParseWorkflowTriggers(associatedWorkflow.WorkflowAssociation.AutoStartCreate, associatedWorkflow.WorkflowAssociation.AutoStartChange, associatedWorkflow.WorkflowAssociation.AllowManual);
                                }

                                WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                                {
                                    SiteColUrl = this.SiteCollectionUrl,
                                    SiteURL = this.SiteUrl,
                                    ListTitle = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.Title : "",
                                    ListUrl = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.RootFolder.ServerRelativeUrl : "",
                                    ListId = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.Id : Guid.Empty,
                                    ContentTypeId = associatedWorkflow.AssociatedContentType != null ? associatedWorkflow.AssociatedContentType.StringId : "",
                                    ContentTypeName = associatedWorkflow.AssociatedContentType != null ? associatedWorkflow.AssociatedContentType.Name : "",
                                    Version = "2010",
                                    Scope = associatedWorkflow.Scope,
                                    RestrictToType = "N/A",
                                    DefinitionName = workflowTemplate.Name,
                                    DefinitionDescription = workflowTemplate.Description,
                                    SubscriptionName = associatedWorkflow.WorkflowAssociation.Name,
                                    HasSubscriptions = true,
                                    Enabled = associatedWorkflow.WorkflowAssociation.Enabled,
                                    DefinitionId = workflowTemplate.Id,
                                    IsOOBWorkflow = IsOOBWorkflow(workflowTemplate.Id.ToString()),

                                    SubscriptionId = associatedWorkflow.WorkflowAssociation.Id,
                                    UsedActions = workFlowAnalysisResult?.WorkflowActions,
                                    ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                    UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                                    UnsupportedActionsInFlow = workFlowAnalysisResult?.UnsupportedActions,
                                    UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                    LastDefinitionEdit = loadedWorkflow != null ? loadedWorkflow.Item2 : associatedWorkflow.WorkflowAssociation.Modified,
                                    LastSubscriptionEdit = associatedWorkflow.WorkflowAssociation.Modified,
                                    // WEB WORKFLOW:
                                    OOBWorkflowName = IsOOBWorkflow(workflowTemplate.Id.ToString()) ? OOBWorkflowNames[workflowTemplate.Id.ToString()] : "",
                                    LastListItemEdit = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.LastItemModifiedDate : new DateTime(1, 1, 1, 0, 0, 0, 0),
                                    LastListItemEditByUser = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.LastItemUserModifiedDate : new DateTime(1, 1, 1, 0, 0, 0, 0),
                                    LastWebItemEdit = web.LastItemModifiedDate,
                                    LastWebItemEditByUser = web.LastItemUserModifiedDate,
                                    UpgradeEfforts = Math.Round(workFlowAnalysisResult != null ? workFlowAnalysisResult.UpgradeEfforts : OOTBWorkflowEfforts(workflowTemplate.Id.ToString()),1),
                                    TotalActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.TotalActionCount : 0
                                };

                                if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                                {
                                    ScanError error = new ScanError()
                                    {
                                        Error = $"Could not add 2010 {associatedWorkflow.Scope} type workflow scan result for {workflowScanResult.SiteColUrl}",
                                        SiteColUrl = this.SiteCollectionUrl,
                                        SiteURL = this.SiteUrl,
                                        Field1 = "WorkflowAnalyzer",
                                    };
                                    this.ScanJob.ScanErrors.Push(error);
                                }
                            }
                        }
                        else
                        {
                            // Only add non OOB workflow templates when there's no associated workflow - makes the dataset smaller
                            if (!IsOOBWorkflow(workflowTemplate.Id.ToString()))
                            {
                                // Perform workflow analysis
                                WorkflowActionAnalysis workFlowAnalysisResult = null;
                                WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                                Tuple<string, DateTime> loadedWorkflow = null;

                                if (Options.IncludeWorkflowWithDetails(this.ScanJob.Mode))
                                {
                                    loadedWorkflow = LoadWorkflowDefinition(cc, workflowTemplate);
                                    if (!string.IsNullOrEmpty(loadedWorkflow?.Item1))
                                    {
                                        workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(loadedWorkflow.Item1, WorkflowTypes.SP2010);
                                    }
                                    workFlowTriggerAnalysisResult = WorkflowManager.Instance.ParseWorkflowTriggers(workflowTemplate.AutoStartCreate, workflowTemplate.AutoStartChange, workflowTemplate.AllowManual);
                                }

                                WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                                {
                                    SiteColUrl = this.SiteCollectionUrl,
                                    SiteURL = this.SiteUrl,
                                    ListTitle = "",
                                    ListUrl = "",
                                    ListId = Guid.Empty,
                                    ContentTypeId = "",
                                    ContentTypeName = "",
                                    Version = "2010",
                                    Scope = "",
                                    RestrictToType = "N/A",
                                    DefinitionName = workflowTemplate.Name,
                                    DefinitionDescription = workflowTemplate.Description,
                                    SubscriptionName = "",
                                    HasSubscriptions = false,
                                    Enabled = false,
                                    DefinitionId = workflowTemplate.Id,
                                    IsOOBWorkflow = IsOOBWorkflow(workflowTemplate.Id.ToString()),
                                    SubscriptionId = Guid.Empty,
                                    UsedActions = workFlowAnalysisResult?.WorkflowActions,
                                    ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                    UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                                    LastDefinitionEdit = loadedWorkflow != null ? loadedWorkflow.Item2 : DateTime.MinValue,
                                };

                                if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                                {
                                    ScanError error = new ScanError()
                                    {
                                        Error = $"Could not add 2010 type workflow scan result for {workflowScanResult.SiteColUrl}",
                                        SiteColUrl = this.SiteCollectionUrl,
                                        SiteURL = this.SiteUrl,
                                        Field1 = "WorkflowAnalyzer",
                                    };
                                    this.ScanJob.ScanErrors.Push(error);
                                }
                            }
                        }
                    }
                }

                // Are there associated workflows for which we did not find a template (especially when the WF is created for a list)
                foreach (var associatedWorkflow in this.sp2010WorkflowAssociations)
                {
                    if (!processedWorkflowAssociations.Contains(associatedWorkflow.WorkflowAssociation.Id))
                    {
                        // Skip previous versions of a workflow
                        // TODO: non-english sites will use another string
                        if (associatedWorkflow.WorkflowAssociation.Name.Contains(PreviousWorkflowString(web.Language)))
                        {
                            continue;
                        }

                        // Perform workflow analysis
                        WorkflowActionAnalysis workFlowAnalysisResult = null;
                        WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                        Tuple<string, DateTime> loadedWorkflow = null;

                        if (Options.IncludeWorkflowWithDetails(this.ScanJob.Mode))
                        {
                            loadedWorkflow = LoadWorkflowDefinition(cc, associatedWorkflow.WorkflowAssociation);
                            if (!string.IsNullOrEmpty(loadedWorkflow?.Item1))
                            {
                                workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(loadedWorkflow.Item1, WorkflowTypes.SP2010);
                            }
                            workFlowTriggerAnalysisResult = WorkflowManager.Instance.ParseWorkflowTriggers(associatedWorkflow.WorkflowAssociation.AutoStartCreate, associatedWorkflow.WorkflowAssociation.AutoStartChange, associatedWorkflow.WorkflowAssociation.AllowManual);
                        }

                        WorkflowScanResult workflowScanResult = new WorkflowScanResult()
                        {
                            SiteColUrl = this.SiteCollectionUrl,
                            SiteURL = this.SiteUrl,
                            ListTitle = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.Title : "",
                            ListUrl = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.RootFolder.ServerRelativeUrl : "",
                            ListId = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.Id : Guid.Empty,
                            ContentTypeId = associatedWorkflow.AssociatedContentType != null ? associatedWorkflow.AssociatedContentType.StringId : "",
                            ContentTypeName = associatedWorkflow.AssociatedContentType != null ? associatedWorkflow.AssociatedContentType.Name : "",
                            Version = "2010",
                            Scope = associatedWorkflow.Scope,
                            RestrictToType = "N/A",
                            DefinitionName = associatedWorkflow.WorkflowAssociation.Name,
                            DefinitionDescription = "",
                            SubscriptionName = associatedWorkflow.WorkflowAssociation.Name,
                            HasSubscriptions = true,
                            Enabled = associatedWorkflow.WorkflowAssociation.Enabled,
                            DefinitionId = Guid.Empty,
                            IsOOBWorkflow = false,
                            SubscriptionId = associatedWorkflow.WorkflowAssociation.Id,
                            UsedActions = workFlowAnalysisResult?.WorkflowActions,
                            ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                            UsedTriggers = workFlowTriggerAnalysisResult?.WorkflowTriggers,
                            LastSubscriptionEdit = associatedWorkflow.WorkflowAssociation.Modified,
                            LastDefinitionEdit = loadedWorkflow != null ? loadedWorkflow.Item2 : associatedWorkflow.WorkflowAssociation.Modified,
                            // Custom List WORKFLOW:
                            OOBWorkflowName = "",
                            LastListItemEditByUser = associatedWorkflow.AssociatedList != null ? associatedWorkflow.AssociatedList.LastItemUserModifiedDate : new DateTime(1, 1, 1, 0, 0, 0, 0),
                            LastWebItemEdit = web.LastItemModifiedDate,
                            LastWebItemEditByUser = web.LastItemUserModifiedDate,
                            UpgradeEfforts = workFlowAnalysisResult != null ? workFlowAnalysisResult.UpgradeEfforts : 0,
                            TotalActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.TotalActionCount : 0



                        };

                        if (!this.ScanJob.WorkflowScanResults.TryAdd($"workflowScanResult.SiteURL.{Guid.NewGuid()}", workflowScanResult))
                        {
                            ScanError error = new ScanError()
                            {
                                Error = $"Could not add 2010 {associatedWorkflow.Scope} type workflow scan result for {workflowScanResult.SiteColUrl}",
                                SiteColUrl = this.SiteCollectionUrl,
                                SiteURL = this.SiteUrl,
                                Field1 = "WorkflowAnalyzer",
                            };
                            this.ScanJob.ScanErrors.Push(error);
                        }
                    }

                }

                #endregion
            }
            catch (Exception ex)
            {
                ScanError error = new ScanError()
                {
                    Error = ex.Message,
                    SiteColUrl = this.SiteCollectionUrl,
                    SiteURL = this.SiteUrl,
                    Field1 = "WorkflowAnalyzer",
                    Field2 = ex.StackTrace,
                };

                // Send error to telemetry to make scanner better
                if (this.ScanJob.ScannerTelemetry != null)
                {
                    this.ScanJob.ScannerTelemetry.LogScanError(ex, error);
                }

                this.ScanJob.ScanErrors.Push(error);
            }
            finally
            {
                this.StopTime = DateTime.Now;
            }

            // return the duration of this scan
            return new TimeSpan((this.StopTime.Subtract(this.StartTime).Ticks));
        }

        internal static void PopulateAdminAndOwnerColumns(ConcurrentDictionary<string, SiteScanResult> siteScanResults, ConcurrentDictionary<string, WorkflowScanResult> workflowScanResults)
        {
            foreach (var workflowScanResult in workflowScanResults)
            {
                if (siteScanResults.ContainsKey(workflowScanResult.Value.SiteColUrl))
                {
                    var siteScanResult = siteScanResults[workflowScanResult.Value.SiteColUrl];
                    workflowScanResult.Value.Admins = siteScanResult.Admins;
                    workflowScanResult.Value.Owners = siteScanResult.Owners;
                }
            }
        }
        #endregion

        #region Helper methods
        private DateTime GetWorkflowPropertyDateTime(IDictionary<string, string> properties, string property)
        {
            if (string.IsNullOrEmpty(property) || properties == null)
            {
                return DateTime.MinValue;
            }

            if (properties.ContainsKey(property))
            {
                if (DateTime.TryParseExact(properties[property], "M/d/yyyy h:m:s tt", new CultureInfo("en-US"), DateTimeStyles.AssumeUniversal, out DateTime parsedValue))
                {
                    return parsedValue;
                }
            }

            return DateTime.MinValue;
        }

        private bool GetWorkflowPropertyBool(IDictionary<string, string> properties, string property)
        {
            if (string.IsNullOrEmpty(property) || properties == null)
            {
                return false;
            }

            if (properties.ContainsKey(property))
            {
                if (bool.TryParse(properties[property], out bool parsedValue))
                {
                    return parsedValue;
                }
            }

            return false;
        }

        private Tuple<string, DateTime> LoadWorkflowDefinition(ClientContext cc, WorkflowAssociation workflowAssociation)
        {
            // Ensure the workflow library was loaded if not yet done
            LoadWorkflowLibrary(cc);
            try
            {
                return GetFileInformation(cc.Web, $"{this.workflowList.RootFolder.ServerRelativeUrl}/{workflowAssociation.Name}/{workflowAssociation.Name}.xoml");
            }
            catch (Exception ex)
            {
            }

            try
            {
                LoadWfPubLibrary(cc);
                return GetFileInformation(cc.Site.RootWeb, $"{this.wfpubList.RootFolder.ServerRelativeUrl}/{workflowAssociation.Name}/{workflowAssociation.Name}.xoml");
            }
            catch (Exception ex)
            {
            }

            return null;
        }

        private Tuple<string, DateTime> LoadWorkflowDefinition(ClientContext cc, WorkflowTemplate workflowTemplate)
        {
            if (!IsOOBWorkflow(workflowTemplate.Id.ToString()))
            {
                // Ensure the workflow library was loaded if not yet done
                LoadWorkflowLibrary(cc);
                try
                {
                    return GetFileInformation(cc.Web, $"{this.workflowList.RootFolder.ServerRelativeUrl}/{workflowTemplate.Name}/{workflowTemplate.Name}.xoml");
                }
                catch (Exception ex)
                {
                }

                try
                {
                    LoadWfPubLibrary(cc);
                    return GetFileInformation(cc.Site.RootWeb, $"{this.wfpubList.RootFolder.ServerRelativeUrl}/{workflowTemplate.Name}/{workflowTemplate.Name}.xoml");
                }
                catch (Exception ex)
                {
                }

            }

            return null;
        }

        private static Tuple<string, DateTime> GetFileInformation(Web web, string serverRelativeUrl)
        {
            var file = web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelativeUrl));

            web.Context.Load(file, p => p.ListItemAllFields);
            web.Context.ExecuteQueryRetry();

            // TODO: fails when using sites.read.all role on xoml file download (access denied, requires ACP permission level)
            ClientResult<Stream> stream = file.OpenBinaryStream();
            web.Context.ExecuteQueryRetry();

            string returnString = string.Empty;
            DateTime date = DateTime.MinValue;

            date = file.ListItemAllFields.LastModifiedDateTime();

            using (Stream memStream = new MemoryStream())
            {
                CopyStream(stream.Value, memStream);
                memStream.Position = 0;
                StreamReader reader = new StreamReader(memStream);
                returnString = reader.ReadToEnd();
            }

            return new Tuple<string, DateTime>(returnString, date);
        }

        private static void CopyStream(Stream source, Stream destination)
        {
            byte[] buffer = new byte[32768];
            int bytesRead;

            do
            {
                bytesRead = source.Read(buffer, 0, buffer.Length);
                destination.Write(buffer, 0, bytesRead);
            } while (bytesRead != 0);
        }

        private List LoadWorkflowLibrary(ClientContext cc)
        {
            if (this.workflowList != null)
            {
                return this.workflowList;
            }

            var baseExpressions = new List<Expression<Func<List, object>>> { l => l.DefaultViewUrl, l => l.Id, l => l.BaseTemplate, l => l.OnQuickLaunch, l => l.DefaultViewUrl, l => l.Title, l => l.Hidden, l => l.RootFolder.ServerRelativeUrl };
            var query = cc.Web.Lists.IncludeWithDefaultProperties(baseExpressions.ToArray());
            //var lists = cc.Web.Context.LoadQuery(query.Where(l => l.Title == "Workflows"));
            var lists = cc.Web.Context.LoadQuery(query.Where(l => l.BaseTemplate == 117));
            cc.ExecuteQueryRetry();
            this.workflowList = lists.FirstOrDefault();

            return this.workflowList;
        }

        private List LoadWfPubLibrary(ClientContext cc)
        {
            if (this.wfpubList != null)
            {
                return this.wfpubList;
            }

            var baseExpressions = new List<Expression<Func<List, object>>> { l => l.DefaultViewUrl, l => l.Id, l => l.BaseTemplate, l => l.OnQuickLaunch, l => l.DefaultViewUrl, l => l.Title, l => l.Hidden, l => l.RootFolder.ServerRelativeUrl };
            var query = cc.Site.RootWeb.Lists.IncludeWithDefaultProperties(baseExpressions.ToArray());
            //var lists = cc.Web.Context.LoadQuery(query.Where(l => l.Title == "Workflows"));
            var lists = cc.LoadQuery(query.Where(l => l.BaseTemplate == 122));
            cc.ExecuteQueryRetry();
            this.wfpubList = lists.FirstOrDefault();

            return this.wfpubList;
        }

        #region Not used code
        //private List LoadWorkflowCatalog(ClientContext cc)
        //{
        //    if (this.workflowCatalog != null)
        //    {
        //        return this.workflowCatalog;
        //    }

        //    //TODO: does this work for sub sites, verify that this library exists on sub sites?
        //    this.workflowCatalog = cc.Web.GetListByTitle("wfpub");
        //    if (this.workflowCatalog != null)
        //    {
        //        //this.workflowCatalog.EnsureProperty(p => p.RootFolder);
        //        this.workflowCatalog.RootFolder.EnsureProperty(p => p.ServerRelativeUrl);
        //    }

        //    return this.workflowCatalog;
        //}
        #endregion

        private string GetWorkflowProperty(WorkflowSubscription subscription, string propertyName)
        {
            if (subscription.PropertyDefinitions.ContainsKey(propertyName))
            {
                return subscription.PropertyDefinitions[propertyName];
            }

            return "";
        }

        private bool IsOOBWorkflow(string workflowTemplateId)
        {
            if (!string.IsNullOrEmpty(workflowTemplateId))
            {
                foreach (var oobId in WorkflowAnalyzer.OOBWorkflowIDStarts)
                {
                    if (workflowTemplateId.StartsWith(oobId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private decimal OOTBWorkflowEfforts(string workflowTemplateId)
        {
            if (IsOOBWorkflow(workflowTemplateId))
            {

                switch (OOBWorkflowNames[workflowTemplateId])
                {
                    case "Publishing approval":
                        return (decimal)2;

                    case "Collect feedback - 2010":
                        return (decimal)2.5;

                    case "Collect feedback":
                        return (decimal)2.5;

                    case "Collect signatures - 2010":
                        return (decimal)3;

                    case "Collect signatures":
                        return (decimal)3;

                    case "Approval - 2010":
                        return (decimal)2;

                    case "Translation management":
                        return (decimal)4;

                    case "Three state":
                        return (decimal)2.5;

                    case "Approval":
                        return (decimal)2;

                    case "Disposition approval":
                        return (decimal)2.5;

                    default:
                        return 0;
                }
            }
            else
            {
                return 0;
            }
        }

        private static string PreviousWorkflowString(uint language)
        {
            switch (language)
            {
                case 1033: return "(Previous Version:";
                case 1164: return "( نسخه گذشته ) :";
                case 2108: return "(An Leagan Roimhe:";
                case 1081: return "(पिछला संस्करण:";
                case 1071: return "(Претходна верзија:";
                case 1049: return "(предыдущая версия:";
                case 1051: return "(predchádzajúca verzia:";
                case 1028: return "(舊版本:";
                case 1058: return "(Попередня версія:";
                case 1027: return "(versió anterior:";
                case 1029: return "(Předchozí verze:";
                case 1030: return "(Tidligere version:";
                case 3082: return "(Versión anterior:";
                case 1069: return "(aurreko bertsioa:";
                case 1038: return "(Előző verzió:";
                case 1057: return "(Versi Sebelumnya:";
                case 1042: return "(이전 버전:";
                case 1063: return "(ankstesnė versija:";
                case 1062: return "(iepriekšējā versija:";
                case 1045: return "(poprzednia wersja:";
                case 2070:
                case 1046: return "(Versão Anterior:";
                case 1048: return "(versiune anterioară:";
                case 1060: return "(Prejšnja različica:";
                case 10266: return "(претходна верзија:";
                case 2074: return "(Prethodna verzija:";
                case 1054: return "(เวอร์ชันก่อนหน้า:";
                case 1055: return "(Önceki Sürüm:";
                case 1025: return "(الإصدار السابق:";
                case 1068: return "(Əvvəlki Versiya:";
                case 1026: return "(Предишна версия:";
                case 1106: return "(Fersiwn Blaenorol:";
                case 1031: return "(Vorherige Version:";
                case 1061: return "(eelmine versioon:";
                case 1035: return "(aiempi versio:";
                case 1036: return "(version précédente :";
                case 1110: return "(Versión anterior:";
                case 1037: return "(גירסה קודמת:";
                case 1050: return "(Prethodna verzija:";
                case 1040: return "(versione precedente:";
                case 1041: return "(以前のバージョン:";
                case 1087: return "(Алдыңғы нұсқа:";
                case 1086: return "(Versi Sebelumnya:";
                case 1044: return "(Tidligere versjon:";
                case 1043: return "(vorige versie:";
                case 1053: return "(Tidigare version:";
                case 9242: return "(Prethodna verzija:";
                case 1032: return "(Προηγούμενη έκδοση:";
                case 1066: return "(Phiên bản Trước:";
                case 2052: return "(以前版本:";
                default:
                    return "(Previous Version:";
            }
        }

        #endregion
    }
}
