﻿using System;
using System.Collections.Generic;

namespace SharePoint.Modernization.Scanner.Core.Workflow
{
    public class WorkflowActionAnalysis
    {

        public WorkflowActionAnalysis()
        {
            this.WorkflowActions = new List<string>();
            this.UnsupportedActions = new List<string>();
        }

        public List<string> WorkflowActions { get; set; }
        public int TotalActionCount { get; set; }
        public int ActionCount { get; set; }
        public List<string> UnsupportedActions { get; set; }
        public int UnsupportedAccountCount { get; set; }
        public decimal UpgradeEfforts { get; set; }

        public DateTime LastListItemEdit { get; set; }
    }
}
