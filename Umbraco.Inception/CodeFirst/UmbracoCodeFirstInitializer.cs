using Epiphany.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Hosting;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Inception.Attributes;
using Umbraco.Inception.BL;
using Umbraco.Inception.Extensions;

namespace Umbraco.Inception.CodeFirst
{
    public class UmbracoCodeFirstInitializer
    {
        readonly IContentTypeService _contentTypeService;
        readonly IFileService _fileService;
        readonly IDataTypeService _dataTypeService;

        public UmbracoCodeFirstInitializer() : this(ApplicationContext.Current.Services.ContentTypeService, ApplicationContext.Current.Services.FileService, ApplicationContext.Current.Services.DataTypeService)
        {
            // Default constructor without DI
        }

        public UmbracoCodeFirstInitializer(IContentTypeService contentSvc, IFileService fileSvc, IDataTypeService dataTypeSvc)
        {
            _contentTypeService = contentSvc;
            _fileService = fileSvc;
            _dataTypeService = dataTypeSvc;
        }

        /// <summary>
        /// This method will create or update the Content Type in Umbraco.
        /// It's possible that you need to run this method a few times to create all relations between Content Types.
        /// </summary>
        /// <param name="type">The type of your model that contains an UmbracoContentTypeAttribute</param>
        public void CreateOrUpdateEntity(Type type)
        {
            var attribute = type.GetCustomAttribute<UmbracoContentTypeAttribute>();
            if (attribute == null) return;

            // ensure any heirarchy of types are persisted, and parent types are already created.
            var types = type.GetBaseTypes(true).WithoutLast().ToList(); // everything inherits object, skip it

            if (types.Last() == typeof(UmbracoGeneratedBase))
            {
                var propertiesToAdd = new List<PropertyInfo>();
                var tabsToAdd = new List<PropertyInfo>();
                var parentAlias = string.Empty;
                for (var i = types.Count - 2; i >= 0; i--) // skip last as it will be UmbracoGeneratedBase
                {
                    var t = types[i];

                    // save the properties and tabs from this type
                    propertiesToAdd.AddRange(t.GetPropertiesWithAttribute<UmbracoPropertyAttribute>());
                    tabsToAdd.AddRange(t.GetPropertiesWithAttribute<UmbracoTabAttribute>());

                    if (!t.IsAbstract)
                    {
                        var typeAttribute = t.GetCustomAttribute<UmbracoContentTypeAttribute>();
                        if (typeAttribute != null)
                        {
                            // create/update this type, and add all inherited properties from abstract classes
                            PersistContentType(t, typeAttribute, parentAlias, propertiesToAdd, tabsToAdd);
                        }
                        else
                        {
                            throw new Exception(string.Format("The type {0} does not have a UmbracoContentTypeAttribute", type.FullName));
                        }

                        parentAlias = typeAttribute.ContentTypeAlias;
                        propertiesToAdd.Clear();
                        tabsToAdd.Clear();
                    }
                }
            }
            else
            {
                throw new Exception("The given type does not inherit from UmbracoGeneratedBase");
            }
        }

        private void PersistContentType(Type type, UmbracoContentTypeAttribute attribute, string parentAlias, IEnumerable<PropertyInfo> propertiesToAdd, IEnumerable<PropertyInfo> tabsToAdd)
        {
            var contentType = _contentTypeService.GetContentType(attribute.ContentTypeAlias) ?? CreateContentType(parentAlias);

            contentType.Name = attribute.ContentTypeName;
            contentType.Alias = attribute.ContentTypeAlias;
            contentType.Icon = attribute.Icon;
            contentType.AllowedAsRoot = attribute.AllowedAtRoot;
            contentType.IsContainer = attribute.EnableListView;
            contentType.AllowedContentTypes = FetchAllowedContentTypes(attribute.AllowedChildren);

            if (attribute.CreateMatchingView)
            {
                CreateMatchingView(attribute, type, contentType);
            }

            //create tabs
            CreateTabs(contentType, type, tabsToAdd);

            //create properties with no tab specified
            var propertiesOfRoot = type.GetPropertiesWithAttribute<UmbracoPropertyAttribute>().Concat(propertiesToAdd);
            foreach (var item in propertiesOfRoot)
            {
                CreateProperty(contentType, null, item);
            }

            if (contentType.Id != 0) // if the content type is not yet saved, no need to do this
            {
                VerifyProperties(contentType, type);

                //verify if a tab has no properties, if so remove
                var propertyGroups = contentType.PropertyGroups.ToArray();
                var length = propertyGroups.Length;
                for (var i = 0; i < length; i++)
                {
                    if (propertyGroups[i].PropertyTypes.Count == 0)
                    {
                        //remove
                        contentType.RemovePropertyGroup(propertyGroups[i].Name);
                    }
                }
            }

            //Save and persist the content Type
            _contentTypeService.Save(contentType);
        }

        #region Create

        /// <summary>
        /// This method is called when the Content Type declared in the attribute hasn't been found in Umbraco
        /// </summary>
        /// <param name="parentAlias"></param>
        private IContentType CreateContentType(string parentAlias)
        {
            var parentContentTypeId = -1;
            if (!string.IsNullOrEmpty(parentAlias))
            {
                var parentContentType = _contentTypeService.GetContentType(parentAlias);
                parentContentTypeId = parentContentType.Id;
            }

            return new ContentType(parentContentTypeId);
        }

        /// <summary>
        /// Creates a View if specified in the attribute
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="type"></param>
        /// <param name="newContentType"></param>
        private void CreateMatchingView(UmbracoContentTypeAttribute attribute, Type type, IContentType newContentType)
        {
            var currentTemplate = _fileService.GetTemplate(attribute.ContentTypeAlias) as Template;
            if (currentTemplate == null)
            {
                string templatePath;
                if (string.IsNullOrEmpty(attribute.TemplateLocation))
                {
                    templatePath = string.Format(CultureInfo.InvariantCulture, "~/Views/{0}.cshtml", attribute.ContentTypeAlias);
                }
                else
                {
                    templatePath = string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}.cshtml",
                        attribute.TemplateLocation,                                     // The template location
                        attribute.TemplateLocation.EndsWith("/") ? string.Empty : "/",  // Ensure the template location ends with a "/"
                        attribute.ContentTypeAlias);                                    // The alias
                }

                currentTemplate = new Template(templatePath, attribute.ContentTypeName, attribute.ContentTypeAlias);
                CreateViewFile(attribute.MasterTemplate, currentTemplate, type);
            }

            newContentType.AllowedTemplates = new ITemplate[] { currentTemplate };
            newContentType.SetDefaultTemplate(currentTemplate);

            //TODO: in Umbraco 7.1 it will be possible to set the master template of the newly created template
            //https://github.com/umbraco/Umbraco-CMS/pull/294
        }

        /// <summary>
        /// Scans for properties on the model which have the UmbracoTab attribute
        /// </summary>
        /// <param name="newContentType"></param>
        /// <param name="model"></param>
        /// <param name="tabsToAdd"></param>
        private void CreateTabs(IContentType newContentType, Type model, IEnumerable<PropertyInfo> tabsToAdd)
        {
            var properties = model.GetPropertiesWithAttribute<UmbracoTabAttribute>().Where(x => x.DeclaringType == model).Concat(tabsToAdd).ToArray();
            var length = properties.Length;

            for (var i = 0; i < length; i++)
            {
                var tabAttribute = properties[i].GetCustomAttribute<UmbracoTabAttribute>();

                newContentType.AddPropertyGroup(tabAttribute.Name);

                CreateProperties(properties[i], newContentType, tabAttribute.Name);
            }
        }

        /// <summary>
        /// Every property on the Tab object is scanned for the UmbracoProperty attribute
        /// </summary>
        /// <param name="propertyInfo"></param>
        /// <param name="newContentType"></param>
        /// <param name="tabName"></param>
        private void CreateProperties(PropertyInfo propertyInfo, IContentType newContentType, string tabName)
        {
            //type is from TabBase
            var type = propertyInfo.PropertyType;
            var properties = type.GetPropertiesWithAttribute<UmbracoPropertyAttribute>().ToList();
            if (properties.Any())
            {
                foreach (var item in properties)
                {
                    CreateProperty(newContentType, tabName, item);
                }
            }
        }

        /// <summary>
        /// Creates a new property on the ContentType under the correct tab
        /// </summary>
        /// <param name="newContentType"></param>
        /// <param name="tabName"></param>
        /// <param name="item"></param>
        private void CreateProperty(IContentTypeBase newContentType, string tabName, MemberInfo item)
        {
            var attribute = item.GetCustomAttribute<UmbracoPropertyAttribute>();

            IDataTypeDefinition dataTypeDef;
            if (string.IsNullOrEmpty(attribute.DataTypeInstanceName))
            {
                dataTypeDef = _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(attribute.DataType).FirstOrDefault();
            }
            else
            {
                dataTypeDef = _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(attribute.DataType).FirstOrDefault(x => x.Name == attribute.DataTypeInstanceName);
            }

            if (dataTypeDef != null)
            {
                var propertyType = new PropertyType(dataTypeDef)
                {
                    Name = attribute.Name,
                    Alias = (string.IsNullOrEmpty(tabName) ? attribute.Alias : UmbracoCodeFirstExtensions.HyphenToUnderscore(UmbracoCodeFirstExtensions.ParseUrl(attribute.Alias + "_" + tabName, false))),
                    Description = attribute.Description,
                    Mandatory = attribute.Mandatory
                };

                if (string.IsNullOrEmpty(tabName))
                {
                    newContentType.AddPropertyType(propertyType);
                }
                else
                {
                    newContentType.AddPropertyType(propertyType, tabName);
                }
            }
        }

        #endregion Create

        #region Update


        /// <summary>
        /// Loop through all properties and remove existing ones if necessary
        /// </summary>
        /// <param name="contentType"></param>
        /// <param name="type"></param>
        private void VerifyProperties(IContentType contentType, Type type)
        {
            var properties = type.GetPropertiesWithAttribute<UmbracoTabAttribute>().ToArray();
            var propertiesThatShouldExist = new List<string>();

            foreach (var propertyTab in properties)
            {
                var tabAttribute = propertyTab.GetCustomAttribute<UmbracoTabAttribute>();
                if (contentType.PropertyGroups.All(x => x.Name != tabAttribute.Name))
                {
                    contentType.AddPropertyGroup(tabAttribute.Name);
                }

                propertiesThatShouldExist.AddRange(VerifyAllPropertiesOnTab(propertyTab, contentType, tabAttribute.Name));
            }

            var propertiesOfRoot = type.GetPropertiesWithAttribute<UmbracoPropertyAttribute>();
            propertiesThatShouldExist.AddRange(propertiesOfRoot.Select(item => VerifyExistingProperty(contentType, null, item, true)));

            //loop through all the properties on the ContentType to see if they should be removed;
            var existingUmbracoProperties = contentType.PropertyTypes.ToArray();
            var length = contentType.PropertyTypes.Count();
            for (var i = 0; i < length; i++)
            {
                if (!propertiesThatShouldExist.Contains(existingUmbracoProperties[i].Alias))
                {
                    //remove the property
                    contentType.RemovePropertyType(existingUmbracoProperties[i].Alias);
                }
            }
        }

        /// <summary>
        /// Scan the properties on tabs
        /// </summary>
        /// <param name="propertyTab"></param>
        /// <param name="contentType"></param>
        /// <param name="tabName"></param>
        /// <returns></returns>
        private IEnumerable<string> VerifyAllPropertiesOnTab(PropertyInfo propertyTab, IContentTypeBase contentType, string tabName)
        {
            var type = propertyTab.PropertyType;
            var properties = type.GetPropertiesWithAttribute<UmbracoPropertyAttribute>().ToList();
            if (properties.Any())
            {
                return properties.Select(item => VerifyExistingProperty(contentType, tabName, item)).ToList();
            }
            return new string[0];
        }

        private string VerifyExistingProperty(IContentTypeBase contentType, string tabName, MemberInfo item, bool atGenericTab = false)
        {
            var attribute = item.GetCustomAttribute<UmbracoPropertyAttribute>();
            IDataTypeDefinition dataTypeDef;
            if (string.IsNullOrEmpty(attribute.DataTypeInstanceName))
            {
                dataTypeDef = _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(attribute.DataType).FirstOrDefault();
            }
            else
            {
                dataTypeDef = _dataTypeService.GetDataTypeDefinitionByPropertyEditorAlias(attribute.DataType).FirstOrDefault(x => x.Name == attribute.DataTypeInstanceName);
            }

            if (dataTypeDef != null)
            {
                PropertyType property;
                var alreadyExisted = contentType.PropertyTypeExists(attribute.Alias);
                // TODO: Added attribute.Tab != null after Generic Properties add, is this bulletproof?
                if (alreadyExisted && attribute.Tab != null)
                {
                    property = contentType.PropertyTypes.FirstOrDefault(x => x.Alias == attribute.Alias);
                }
                else
                {
                    property = new PropertyType(dataTypeDef);
                }

                property.Name = attribute.Name;
                //TODO: correct name?
                property.Alias = (atGenericTab ? attribute.Alias : UmbracoCodeFirstExtensions.HyphenToUnderscore(UmbracoCodeFirstExtensions.ParseUrl(attribute.Alias + "_" + tabName, false)));
                property.Description = attribute.Description;
                property.Mandatory = attribute.Mandatory;

                if (!alreadyExisted)
                {
                    if (atGenericTab)
                    {
                        contentType.AddPropertyType(property);
                    }
                    else
                    {
                        contentType.AddPropertyType(property, tabName);
                    }
                }

                return property.Alias;
            }
            return null;
        }

        #endregion Update

        #region Shared logic

        /// <summary>
        /// Gets the allowed children
        /// </summary>
        /// <param name="types"></param>
        /// <returns></returns>
        private IEnumerable<ContentTypeSort> FetchAllowedContentTypes(IEnumerable<Type> types)
        {
            if (types == null) return new ContentTypeSort[0];

            var contentTypeSorts = new List<ContentTypeSort>();

            var aliases = GetAliasesFromTypes(types);

            var contentTypes = _contentTypeService.GetAllContentTypes().Where(x => aliases.Contains(x.Alias)).ToArray();

            var length = contentTypes.Length;
            for (var i = 0; i < length; i++)
            {
                var id = contentTypes[i].Id;
                var sort = new ContentTypeSort
                {
                    Alias = contentTypes[i].Alias,
                    Id = new Lazy<int>(() => id),
                    SortOrder = i
                };
                contentTypeSorts.Add(sort);
            }
            return contentTypeSorts;
        }

        private static List<string> GetAliasesFromTypes(IEnumerable<Type> types)
        {
            return (from type in types select type.GetCustomAttribute<UmbracoContentTypeAttribute>() into attribute where attribute != null select attribute.ContentTypeAlias).ToList();
        }

        private void CreateViewFile(string masterTemplate, Template template, Type type)
        {
            var physicalViewFileLocation = HostingEnvironment.MapPath(template.Path);
            if (string.IsNullOrEmpty(physicalViewFileLocation))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Failed to {0} to a physical location", template.Path));
            }

            var templateContent = CreateDefaultTemplateContent(masterTemplate, type);
            template.Content = templateContent;

            using (var sw = System.IO.File.CreateText(physicalViewFileLocation))
            {
                sw.Write(templateContent);
            }

            //This code doesn't work because template.MasterTemplateId is defined internal
            //I'll do a pull request to change this
            //TemplateNode rootTemplate = fileService.GetTemplateNode(master);
            //template.MasterTemplateId = new Lazy<int>(() => { return rootTemplate.Template.Id; });
            _fileService.SaveTemplate(template);

            //    //TODO: in Umbraco 7.1 it will be possible to set the master template of the newly created template
            //    //https://github.com/umbraco/Umbraco-CMS/pull/294
        }

        private static string CreateDefaultTemplateContent(string master, Type type)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@inherits Umbraco.Web.Mvc.UmbracoTemplatePage");
            sb.AppendLine("@*@using Qite.Umbraco.CodeFirst.Extensions;*@");
            sb.AppendLine("@{");
            sb.AppendLine("\tLayout = \"" + master + ".cshtml\";");
            sb.AppendLine("\t//" + type.Name + " model = Model.Content.ConvertToRealModel<" + type.Name + ">();");
            sb.AppendLine("}");

            return sb.ToString();
        }

        #endregion Shared logic
    }
}