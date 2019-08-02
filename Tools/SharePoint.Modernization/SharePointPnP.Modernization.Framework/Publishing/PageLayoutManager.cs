﻿using Microsoft.SharePoint.Client;
using SharePointPnP.Modernization.Framework.Cache;
using SharePointPnP.Modernization.Framework.Telemetry;
using SharePointPnP.Modernization.Framework.Transform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace SharePointPnP.Modernization.Framework.Publishing
{
    /// <summary>
    /// Class used to manage SharePoint Publishing page layouts
    /// </summary>
    public class PageLayoutManager: BaseTransform
    {
        
        #region Construction
       
        /// <summary>
        /// Constructs the page layout manager class
        /// </summary>
        /// <param name="logObservers">Currently in use log observers</param>
        public PageLayoutManager(IList<ILogObserver> logObservers = null)
        {

            // Register observers
            if (logObservers != null)
            {
                foreach (var observer in logObservers)
                {
                    base.RegisterObserver(observer);
                }
            }
        }
        #endregion

        /// <summary>
        /// Loads a page layout mapping file
        /// </summary>
        /// <param name="pageLayoutMappingFile">Path and name of the page mapping file</param>
        /// <returns>A <see cref="PublishingPageTransformation"/> instance.</returns>
        public PublishingPageTransformation LoadPageLayoutMappingFile(string pageLayoutMappingFile)
        {
            LogInfo(string.Format(LogStrings.CustomPageLayoutMappingFileProvided, pageLayoutMappingFile));

            if (!System.IO.File.Exists(pageLayoutMappingFile))
            {
                LogError(string.Format(LogStrings.Error_PageLayoutMappingFileDoesNotExist, pageLayoutMappingFile), LogStrings.Heading_PageLayoutManager);
                throw new ArgumentException(string.Format(LogStrings.Error_PageLayoutMappingFileDoesNotExist, pageLayoutMappingFile));
            }

            using (Stream schema = typeof(PageLayoutManager).Assembly.GetManifestResourceStream("SharePointPnP.Modernization.Framework.Publishing.pagelayoutmapping.xsd"))
            {
                XmlSerializer xmlMapping = new XmlSerializer(typeof(PublishingPageTransformation));
                using (var stream = new FileStream(pageLayoutMappingFile, FileMode.Open))
                {
                    // Ensure the provided custom files complies with the schema
                    ValidateSchema(schema, stream);

                    // Seems the file is good...
                    return (PublishingPageTransformation)xmlMapping.Deserialize(stream);
                }
            }
        }

        /// <summary>
        /// Load the default page layout mapping file
        /// </summary>
        /// <returns>A <see cref="PublishingPageTransformation"/> instance.</returns>
        internal PublishingPageTransformation LoadDefaultPageLayoutMappingFile()
        {
            var fileContent = "";
            using (Stream stream = typeof(PageLayoutManager).Assembly.GetManifestResourceStream("SharePointPnP.Modernization.Framework.Publishing.pagelayoutmapping.xml"))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    fileContent = reader.ReadToEnd();
                }
            }

            XmlSerializer xmlMapping = new XmlSerializer(typeof(PublishingPageTransformation));
            using (var stream = GenerateStreamFromString(fileContent))
            {
                return (PublishingPageTransformation)xmlMapping.Deserialize(stream);
            }          
        }

        /// <summary>
        /// Load to PageLayout that will be used to transform the given page
        /// </summary>
        /// <param name="publishingPageTransformation">Publishing page transformation data to get the correct page layout mapping from</param>
        /// <param name="page">Page for which we're looking for a mapping</param>
        /// <returns>The page layout mapping that will be used for the passed page</returns>
        public PageLayout GetPageLayoutMappingModel(PublishingPageTransformation publishingPageTransformation, ListItem page)
        {
            // Load relevant model data for the used page layout
            string usedPageLayout = Path.GetFileNameWithoutExtension(page.PageLayoutFile());
            var publishingPageTransformationModel = publishingPageTransformation.PageLayouts.Where(p => p.Name.Equals(usedPageLayout, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

            // No layout provided via either the default mapping or custom mapping file provided
            if (publishingPageTransformationModel == null)
            {
                publishingPageTransformationModel = CacheManager.Instance.GetPageLayoutMapping(page);
            }

            // Still no layout...can't continue...
            if (publishingPageTransformationModel == null)
            {
                LogError(string.Format(LogStrings.Error_NoPageLayoutTransformationModel, usedPageLayout), LogStrings.Heading_PageLayoutManager);
                throw new Exception(string.Format(LogStrings.Error_NoPageLayoutTransformationModel, usedPageLayout));
            }

            return publishingPageTransformationModel;
        }

        internal PublishingPageTransformation MergePageLayoutMappingFiles(PublishingPageTransformation oobMapping, PublishingPageTransformation customMapping)
        {
            PublishingPageTransformation merged = new PublishingPageTransformation();

            // Handle the page layouts
            List<PageLayout> pageLayouts = new List<PageLayout>();
            foreach (var oobPageLayout in oobMapping.PageLayouts.ToList())
            {
                // If there's the same page layout used in the custom mapping then that one overrides the default
                if (!customMapping.PageLayouts.Where(p=>p.Name.Equals(oobPageLayout.Name, StringComparison.InvariantCultureIgnoreCase)).Any())
                {
                    pageLayouts.Add(oobPageLayout);
                }
            }
            
            // Take over the custom ones
            pageLayouts.AddRange(customMapping.PageLayouts);
            merged.PageLayouts = pageLayouts.ToArray();

            // Handle the add-ons
            merged.AddOns = customMapping.AddOns;

            return merged;
        }

        #region Helper methods
        private void ValidateSchema(Stream schema, FileStream stream)
        {
            // Load the template into an XDocument
            XDocument xml = XDocument.Load(stream);

            // Prepare the XML Schema Set
            XmlSchemaSet schemas = new XmlSchemaSet();
            schema.Seek(0, SeekOrigin.Begin);
            schemas.Add(Constants.PageLayoutMappingSchema, new XmlTextReader(schema));
            
            // Set stream back to start
            stream.Seek(0, SeekOrigin.Begin);

            xml.Validate(schemas, (o, e) =>
            {
                LogError(string.Format(LogStrings.Error_MappingFileSchemaValidation, e.Message), LogStrings.Heading_PageLayoutManager, e.Exception);
                throw new Exception(string.Format(LogStrings.Error_MappingFileSchemaValidation, e.Message));
            });
        }

        /// <summary>
        /// Transforms a string into a stream
        /// </summary>
        /// <param name="s">String to transform</param>
        /// <returns>Stream</returns>
        private static Stream GenerateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }
        #endregion
    }
}
