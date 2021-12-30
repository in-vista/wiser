﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Api.Core.Helpers;
using Api.Core.Interfaces;
using Api.Core.Services;
using Api.Modules.Customers.Interfaces;
using Api.Modules.Templates.Interfaces;
using Api.Modules.Templates.Models;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.GclReplacements.Interfaces;
using GeeksCoreLibrary.Modules.Templates.Enums;
using GeeksCoreLibrary.Modules.Templates.Models;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace Api.Modules.Templates.Services
{
    /// <inheritdoc cref="ITemplatesService" />
    public class TemplatesService : ITemplatesService, IScopedService
    {
        //The list of hardcodes query-strings
        private static readonly Dictionary<string, string> TemplateQueryStrings = new();

        private readonly IWiserCustomersService wiserCustomersService;
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IStringReplacementsService stringReplacementsService;
        private readonly GeeksCoreLibrary.Modules.Templates.Interfaces.ITemplatesService gclTemplatesService;
        private readonly IDatabaseConnection clientDatabaseConnection;
        private readonly IDatabaseConnection wiserDatabaseConnection;
        private readonly IApiReplacementsService apiReplacementsService;

        /// <summary>
        /// Creates a new instance of TemplatesService.
        /// </summary>
        public TemplatesService(IWiserCustomersService wiserCustomersService, IHttpContextAccessor httpContextAccessor, IStringReplacementsService stringReplacementsService, GeeksCoreLibrary.Modules.Templates.Interfaces.ITemplatesService gclTemplatesService, IDatabaseConnection clientDatabaseConnection, IApiReplacementsService apiReplacementsService)
        {
            this.wiserCustomersService = wiserCustomersService;
            this.httpContextAccessor = httpContextAccessor;
            this.stringReplacementsService = stringReplacementsService;
            this.gclTemplatesService = gclTemplatesService;
            this.clientDatabaseConnection = clientDatabaseConnection;
            this.apiReplacementsService = apiReplacementsService;

            if (clientDatabaseConnection is ClientDatabaseConnection connection)
            {
                wiserDatabaseConnection = connection.WiserDatabaseConnection;
            }
        }

        /// <inheritdoc />
        public ServiceResult<Template> Get(int templateId = 0, string templateName = null, string rootName = "")
        {
            if (templateId <= 0 && String.IsNullOrWhiteSpace(templateName))
            {
                throw new ArgumentException("No template ID or name entered.");
            }
            
            string groupingKey = null;
            string groupingPrefix = null;
            var groupingCreateObjectInsteadOfArray = false;
            var groupingKeyColumnName = "";
            var groupingValueColumnName = "";

            var content = TryGetTemplateQuery(templateName, ref groupingKey, ref groupingPrefix, ref groupingCreateObjectInsteadOfArray, ref groupingKeyColumnName, ref groupingValueColumnName);
            
            return new ServiceResult<Template>(new Template
            {
                Id = templateId,
                Name = templateName,
                Content = content
            });
        }

        /// <inheritdoc />
        public Task<ServiceResult<QueryTemplate>> GetQueryAsync(int templateId = 0, string templateName = null)
        {
            var result = GetQueryTemplate(templateId, templateName);

            return Task.FromResult(new ServiceResult<QueryTemplate>(result));
        }
        
        /// <inheritdoc />
        public async Task<ServiceResult<JToken>> GetAndExecuteQueryAsync(ClaimsIdentity identity, string templateName, IFormCollection requestPostData = null)
        {
            var customer = (await wiserCustomersService.GetSingleAsync(identity)).ModelObject;

            // Set the encryption key for the GCL internally. The GCL can't know which key to use otherwise.
            GclSettings.Current.QueryTemplatesDecryptionKey = customer.EncryptionKey;
            
            var queryTemplate = GetQueryTemplate(0, templateName);
            queryTemplate.Content = apiReplacementsService.DoIdentityReplacements(queryTemplate.Content, identity, true);
            
            if (requestPostData != null && requestPostData.Keys.Any())
            {
                queryTemplate.Content = stringReplacementsService.DoReplacements(queryTemplate.Content, requestPostData, true);
            }
            
            var result = await gclTemplatesService.GetJsonResponseFromQueryAsync(queryTemplate, customer.EncryptionKey);
            return new ServiceResult<JToken>(result);
        }

        private QueryTemplate GetQueryTemplate(int templateId = 0, string templateName = null)
        {
            if (templateId <= 0 && String.IsNullOrWhiteSpace(templateName))
            {
                throw new ArgumentException("No template ID or name entered.");
            }
            
            string groupingKey = null;
            string groupingPrefix = null;
            var groupingCreateObjectInsteadOfArray = false;
            var groupingKeyColumnName = "";
            var groupingValueColumnName = "";

            var content = TryGetTemplateQuery(templateName, ref groupingKey, ref groupingPrefix, ref groupingCreateObjectInsteadOfArray, ref groupingKeyColumnName, ref groupingValueColumnName);
            
            var result = new QueryTemplate
            {
                Id = templateId,
                Name = templateName,
                Content = content,
                Type = TemplateTypes.Query,
                GroupingSettings = new QueryGroupingSettings {
                    GroupingColumn = groupingKey,
                    GroupingFieldsPrefix = groupingPrefix,
                    ObjectInsteadOfArray = groupingCreateObjectInsteadOfArray,
                    GroupingKeyColumnName = groupingKeyColumnName,
                    GroupingValueColumnName = groupingValueColumnName
                }
            };

            return result;
        }

        /// <inheritdoc />
        public async Task<ServiceResult<string>> GetCssForHtmlEditorsAsync(ClaimsIdentity identity)
        {
            var outputCss = new StringBuilder();

            // Get stylesheets that are marked to load on every page.
            await clientDatabaseConnection.EnsureOpenConnectionForReadingAsync();
            var dataTable = await clientDatabaseConnection.GetAsync(@"SELECT t.itemid, IF(t.templatetype = 'scss', t.html_minified, t.template) AS template, i.volgnr, t.development
                                                                        FROM easy_templates t
                                                                        JOIN (SELECT MAX(version) AS version, itemid FROM easy_templates GROUP BY itemid) t2 ON t2.itemid = t.itemid AND t2.version = t.version
                                                                        JOIN easy_items i ON i.id = t.itemid AND i.moduleid = 143
                                                                        WHERE t.templatetype IN ('css', 'scss') AND (t.loadalways=1 OR useinwiserhtmleditors=1)
                                                                        ORDER BY volgnr, development DESC");

            if (dataTable.Rows.Count > 0)
            {
                foreach (DataRow dataRow in dataTable.Rows)
                {
                    outputCss.Append(dataRow.Field<string>("template"));
                }
            }

            // Replace URL to the domain.
            dataTable = await clientDatabaseConnection.GetAsync("SELECT `key`, `value` FROM easy_objects WHERE `key` IN ('maindomain', 'requiressl', 'maindomain_wiser')");
            var mainDomain = "";
            var requireSsl = false;
            var mainDomainWiser = "";
            var domainName = "";

            if (dataTable.Rows.Count > 0)
            {
                foreach (DataRow dataRow in dataTable.Rows)
                {
                    var key = dataRow.Field<string>("key");
                    var value = dataRow.Field<string>("value");
                    switch (key.ToLowerInvariant())
                    {
                        case "maindomain":
                            mainDomain = value;
                            break;
                        case "requiressl":
                            requireSsl = String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "maindomain_wiser":
                            mainDomainWiser = value;
                            break;
                    }
                }

                if (!String.IsNullOrWhiteSpace(mainDomain))
                {
                    domainName = $"{(requireSsl ? "https" : "http")}://{mainDomain}/";
                }
                else if (!String.IsNullOrWhiteSpace(mainDomainWiser))
                {
                    domainName = $"{(requireSsl ? "https" : "http")}://{mainDomainWiser}/";
                }
            }

            // Get stylesheets from Wiser.
            dataTable = await clientDatabaseConnection.GetAsync(@"SELECT t.template
                                                                       FROM easy_templates t
                                                                       JOIN (
	                                                                       SELECT i.id, MAX(t.version) AS v
	                                                                       FROM easy_templates t
	                                                                       JOIN easy_items i ON t.itemid = i.id AND i.moduleid = 143 AND i.name IN('shopwarepro', 'wiser')
	                                                                       GROUP BY i.id
                                                                       ) v ON v.v = t.version AND v.id = t.itemid");
            
            if (dataTable.Rows.Count > 0)
            {
                foreach (DataRow dataRow in dataTable.Rows)
                {
                    outputCss.Append(dataRow.Field<string>("template"));
                }
            }

            outputCss = outputCss.Replace("(../", $"({domainName}");
            outputCss = outputCss.Replace("url('fonts", $"url('{domainName}css/fonts");
            
            return new ServiceResult<string>(outputCss.ToString());
        }

        /// <summary>
        /// Return the query-string as it was formally stored in the database. These strings are now hardcoded. 
        /// Settings are also hardcoded now.
        /// </summary>
        /// <param name="templateName"></param>
        /// <param name="groupingCreateObjectInsteadOfArray"></param>
        /// <param name="groupingKey"></param>
        /// <param name="groupingPrefix"></param>
        /// <param name="groupingKeyColumnName"></param>
        /// <param name="groupingValueColumnName"></param>
        /// <returns></returns>
        private string TryGetTemplateQuery(string templateName, ref string groupingKey, ref string groupingPrefix, ref bool groupingCreateObjectInsteadOfArray, ref string groupingKeyColumnName, ref string groupingValueColumnName)
        {
            //make sure the queries are set
            InitTemplateQueries();

            //add special settings
            if (TemplateQueryStrings.ContainsKey(templateName))
            {
                if (new List<string>()
                    {
                        "SEARCH_ITEMS_OLD",
                        "GET_ITEM_DETAILS",
                        "GET_DESTINATION_ITEMS",
                        "GET_DESTINATION_ITEMS_REVERSED",
                        "GET_DATA_FOR_TABLE",
                        "GET_DATA_FOR_FIELD_TABLE"
                    }.Contains(templateName))
                {
                    groupingKey = "id";
                    groupingPrefix = "property_";
                    groupingCreateObjectInsteadOfArray = true;
                    groupingKeyColumnName = "name";
                    groupingValueColumnName = "value";
                }
                else if (templateName.Equals("GET_ITEM_META_DATA"))
                {
                    groupingKey = "id";
                    groupingPrefix = "permission";
                }
                else if (templateName.Equals("GET_CONTEXT_MENU"))
                {
                    groupingKey = "text";
                    groupingPrefix = "attr";
                }
                else if (templateName.Equals("GET_COLUMNS_FOR_LINK_TABLE"))
                {
                    groupingCreateObjectInsteadOfArray = true;
                }

                return TemplateQueryStrings[templateName];
            }

            return null;
        }

        /// <summary>
        /// Hardcode query-strings that before where stored in the database.
        /// </summary>
        private static void InitTemplateQueries()
        {
            if (TemplateQueryStrings.Count == 0)
            {
                //MYSQL-QUERY TO GENERATE THE CODE TO FILL THE DICTIONARY
                //select CONCAT('TemplateQueryStrings.Add("', name, '", @"', REPLACE(template, '"', '""'), '");') from easy_templates where binary upper(name) = name and COALESCE(trim(name), "") != "" and deleted = 0
                //and version = (select MAX(version) from easy_templates M where M.name = easy_templates.name and M.deleted = 0)    //M.itemid = easy_templates.itemid => is itemid important here?

                //load all the template queries into the dictionary
                TemplateQueryStrings.Add("GET_DATA_FOR_RADIO_BUTTONS", @"SET @_itemId = {itemId};
SET @entityproperty_id = {propertyid};
SET @querytext = (SELECT data_query FROM wiser_entityproperty WHERE id=@entityproperty_id);

PREPARE stmt1 FROM @querytext;
EXECUTE stmt1; #USING @itemid;");
                TemplateQueryStrings.Add("GET_ITEM_HTML_FOR_NEW_ITEMS", @"/********* IMPORTANT NOTE: If you change something in this query, please also change it in the query 'GET_ITEM_HTML' *********/
SET SESSION group_concat_max_len = 1000000;
SET @_moduleId = {moduleId};
SET @_entityType = '{entityType}';

SELECT 
	e.tab_name,
    
	GROUP_CONCAT(
        REPLACE(
            REPLACE(
                REPLACE(
                    REPLACE(
                         REPLACE(
                             REPLACE(
                                 REPLACE(
                                   REPLACE(
                                     REPLACE(
                                       REPLACE(
                                         REPLACE(t.html_template, '{title}', IFNULL(e.display_name,e.property_name))
                                       ,'{options}', IFNULL(e.options, ''))
                                     ,'{hint}', IFNULL(e.explanation,''))
                                   ,'{default_value}', IFNULL(e.default_value, ''))
                                 ,'{propertyId}', CONCAT('NEW_', e.id))
                             ,'{itemId}', 0)
                         ,'{propertyName}', e.property_name)
                    ,'{extraAttribute}', IF(IFNULL(e.default_value, 0) > 0, 'checked', ''))
                ,'{dependsOnField}', IFNULL(e.depends_on_field, ''))
            ,'{dependsOnOperator}', IFNULL(e.depends_on_operator, ''))
        ,'{dependsOnValue}', IFNULL(e.depends_on_value, ''))
       ORDER BY e.tab_name ASC, e.ordering ASC SEPARATOR '') AS html_template,
            
        GROUP_CONCAT(
            REPLACE(
                REPLACE(
                    REPLACE(
                           REPLACE(
                               REPLACE(
                                   REPLACE(
                                       REPLACE(
                                           REPLACE(
                                              REPLACE(t.script_template, '{propertyId}', CONCAT('NEW_', e.id)), 
                                                 '{default_value}', CONCAT(""'"", REPLACE(IFNULL(e.default_value, """"), "","", ""','""), ""'"")
                                              ),
                                       '{options}', IF(e.options IS NULL OR e.options = '', '{}', e.options)),
                                   '{propertyName}', e.property_name),
                               '{itemId}', 0),
                           '{title}', IFNULL(e.display_name, e.property_name)),
                        '{dependsOnField}', IFNULL(e.depends_on_field, '')),
                    '{dependsOnOperator}', IFNULL(e.depends_on_operator, '')),
                '{dependsOnValue}', IFNULL(e.depends_on_value, ''))
           ORDER BY e.tab_name ASC, e.ordering ASC SEPARATOR '') AS script_template
            
FROM wiser_entityproperty e
JOIN wiser_field_templates t ON t.field_type = e.inputtype
WHERE e.module_id = @_moduleId
AND e.entity_name = @_entityType
GROUP BY e.tab_name
ORDER BY e.tab_name ASC, e.ordering ASC");
                TemplateQueryStrings.Add("GET_EMAIL_TEMPLATES", @"# Module ID 64 is MailTemplates
# Using texttypes 60 (subject) and 61 (content)

SELECT
    i.id AS template_id,
    i.`name` AS template_name,
    s.content AS email_subject,
    c.content AS email_content
FROM easy_items i
JOIN item_content s ON s.item_id = i.id AND s.texttype_id = 60
JOIN item_content c ON c.item_id = i.id AND c.texttype_id = 61
WHERE i.moduleid = 64
GROUP BY i.id");
                TemplateQueryStrings.Add("SCHEDULER_GET_TEACHERS", @"SELECT 
	""Kevin Manders"" AS `text`,
    1 AS `value`
UNION
	SELECT 
	""Test Docent 2"" ,
    2
UNION
	SELECT 
	""Test Docent 3"" ,
    3
UNION
	SELECT 
	""Test Docent 4"" ,
    4");
                TemplateQueryStrings.Add("SCHEDULER_UPDATE_FAVORITE", @"# insert ignore to favourite

SET @user_id = '{userId}'; # for now always 1
SET @favorite_id = '{favoriteId}';
SET @search_input = '{search}';
SET @view_type = '{type}';
SET @set_teacher = '{teacher}';
SET @set_category = '{category}';
SET @set_location = '{location}';

INSERT INTO schedule_favorites (user_id, favorite_id, search, view_type, teacher, category, location)
VALUES(
    @user_id, 
    @favorite_id, 
    @search_input, 
    @view_type, 
    @set_teacher, 
    @set_category, 
    @set_location)
ON DUPLICATE KEY UPDATE 
	search = @search_input, 
    view_type = @view_type, 
    teacher = @set_teacher, 
    category = @set_category, 
    location = @set_location;
SELECT 1;");

                TemplateQueryStrings.Add("SET_COMMUNICATION_DATA_SELECTOR", @"SET @_communication_id = {itemId};
SET @_dataselector_id = {dataSelectorId};

UPDATE wiser_communication
SET receiver_selectionid = @_dataselector_id
WHERE id = @_communication_id;

SELECT ROW_COUNT() > 0 AS updateSuccessful;");
                TemplateQueryStrings.Add("GET_ENTITY_TYPES", @"SET @_module_list = IF('{modules}' LIKE '{%}', '', '{modules}');

SELECT DISTINCT `name` AS entity_type
FROM wiser_entity
WHERE
    `name` <> ''
    AND IF(@_module_list = '', 1 = 1, FIND_IN_SET(module_id, @_module_list)) > 0
ORDER BY `name`");
                TemplateQueryStrings.Add("CHECK_DATA_SELECTOR_NAME_EXISTS", @"SET @_name = '{name}';

# Will automatically be NULL if it doesn't exist, which is good.
SET @_item_id = (SELECT id FROM wiser_data_selector WHERE `name` = @_name LIMIT 1);

SELECT @_item_id IS NOT NULL AS nameExists;");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_LINKED_TO", @"################################################
#                                              #
#   NOTE: THIS QUERY IS DEPRECATED!            #
#   USE THE '_DOWN' OR '_UP' VERSION INSTEAD   #
#                                              #
################################################

SET @_module_list = IF('{modules}' LIKE '{%}', '', '{modules}');
SET @_entity_type_list = IF('{entity_types}' LIKE '{%}', '', '{entity_types}');

SELECT
    display_name AS `name`,
    CAST(IF(
        inputtype = 'item-linker',
        JSON_OBJECT(
            'inputType', inputtype,
            'type', `options`->>'$.linkTypeName',
            'entityTypes', (SELECT GROUP_CONCAT(DISTINCT entity_name) FROM wiser_itemlink WHERE type_name = `options`->>'$.linkTypeName'),
            'moduleId', `options`->>'$.moduleId'
        ),
        JSON_OBJECT(
            'inputType', inputtype,
            'type', `options`->>'$.entityType',
            'entityTypes', `options`->>'$.entityType'
        )
    ) AS CHAR) AS `options`
FROM wiser_entityproperty
WHERE
    IF(@_module_list = '', 1 = 1, FIND_IN_SET(module_id, @_module_list))
    AND FIND_IN_SET(entity_name, @_entity_type_list)
    AND inputtype IN ('item-linker', 'sub-entities-grid')
ORDER BY display_name");
                TemplateQueryStrings.Add("GET_WISER_TEMPLATES", @"SELECT
    i.id AS template_id,
    i.title AS template_name,
    '' AS email_subject,
    IF(d.long_value IS NULL OR d.long_value = '', d.`value`, d.long_value) AS email_content
FROM wiser_item i
JOIN wiser_itemdetail d ON d.item_id = i.id AND d.`key` = 'html_template'
WHERE i.entity_type = 'template'
ORDER BY i.title");
                TemplateQueryStrings.Add("GET_PROPERTY_VALUES", @"SELECT wid.`value` AS `text`, wid.`value`
FROM wiser_item wi
JOIN wiser_itemdetail wid ON wid.item_id = wi.id
WHERE wi.entity_type = '{entity_name}' AND wid.`key` = '{property_name}' AND wid.`value` <> ''
GROUP BY wid.`value`
ORDER BY wid.`value`
LIMIT 25");
                TemplateQueryStrings.Add("SAVE_COMMUNICATION_ITEM", @"SET @_item_id = '{id}';
SET @_name = '{name}';
SET @_receiver_list = '{receiverList}';
SET @_send_email = {sendEmail};
SET @_email_templateid = {emailTemplateId};
SET @_send_sms = {sendSms};
SET @_send_whatsapp = {sendWhatsApp};
SET @_create_pdf = {createPdf};
SET @_email_subject = '{emailSubject}';
SET @_email_content = '{emailContent_urldataunescape}';
SET @_email_address_selector = '{emailAddressSelector}';
SET @_sms_content = '{smsContent}';
SET @_phone_number_selector = '{phoneNumberSelector}';
SET @_pdf_templateid = {pdfTemplateId};
SET @_send_trigger = '{sendTrigger}';

SET @_senddate = IF('{sendDate}' LIKE '{%}', NULL, '{sendDate}');
SET @_trigger_start = IF('{triggerStart}' LIKE '{%}', NULL, '{triggerStart}');
SET @_trigger_end = IF('{triggerEnd}' LIKE '{%}', NULL, '{triggerEnd}');
SET @_trigger_time = IF('{triggerTime}' LIKE '{%}', NULL, '{triggerTime}');
SET @_trigger_periodvalue = IF('{triggerPeriodValue}' LIKE '{%}', 0, CAST('{triggerPeriodValue}' AS SIGNED));
SET @_trigger_period = IF('{triggerPeriodValue}' LIKE '{%}', 'day', '{triggerPeriod}');
SET @_trigger_periodbeforeafter = IF('{triggerPeriodBeforeAfter}' LIKE '{%}', 'before', '{triggerPeriodBeforeAfter}');
SET @_trigger_days = IF('{triggerDays}' LIKE '{%}', '', '{triggerDays}');
SET @_trigger_type = IF('{triggerType}' LIKE '{%}', 0, CAST('{triggerType}' AS SIGNED));

# If it's a new item, then item_id should be NULL.
SET @_item_id = IF(@_item_id LIKE '{%}' OR @_item_id = '0', NULL, CAST(@_item_id AS SIGNED));

INSERT INTO wiser_communication (id, `name`, receiver_list, send_email, email_templateid, send_sms, send_whatsapp, create_pdf, `email-subject`, `email-content`, email_address_selector, `sms-content`, phone_number_selector, pdf_templateid, send_trigger, senddate, trigger_start, trigger_end, trigger_time, trigger_periodvalue, trigger_period, trigger_periodbeforeafter, trigger_days, trigger_type)
VALUES(@_item_id, @_name, @_receiver_list, @_send_email, @_email_templateid, @_send_sms, @_send_whatsapp, @_create_pdf, @_email_subject, @_email_content, @_email_address_selector, @_sms_content, @_phone_number_selector, @_pdf_templateid, @_send_trigger, @_senddate, @_trigger_start, @_trigger_end, @_trigger_time, @_trigger_periodvalue, @_trigger_period, @_trigger_periodbeforeafter, @_trigger_days, @_trigger_type)
ON DUPLICATE KEY UPDATE
    receiver_list = VALUES(receiver_list),
    send_email = VALUES(send_email),
    email_templateid = VALUES(email_templateid),
    send_sms = VALUES(send_sms),
    send_whatsapp = VALUES(send_whatsapp),
    create_pdf = VALUES(create_pdf),
    `email-subject` = VALUES(`email-subject`),
    `email-content` = VALUES(`email-content`),
    email_address_selector = VALUES(email_address_selector),
    `sms-content` = VALUES(`sms-content`),
    phone_number_selector = VALUES(phone_number_selector),
    pdf_templateid = VALUES(pdf_templateid),
    send_trigger = VALUES(send_trigger),
    senddate = VALUES(senddate),
    trigger_start = VALUES(trigger_start),
    trigger_end = VALUES(trigger_end),
    trigger_time = VALUES(trigger_time),
    trigger_periodvalue = VALUES(trigger_periodvalue),
    trigger_period = VALUES(trigger_period),
    trigger_periodbeforeafter = VALUES(trigger_periodbeforeafter),
    trigger_days = VALUES(trigger_days),
    trigger_type = VALUES(trigger_type);

SELECT IF(@_item_id IS NULL, LAST_INSERT_ID(), @_item_id) AS newId;");
                TemplateQueryStrings.Add("CHECK_COMMUNICATION_NAME_EXISTS", @"SET @_name = '{name}';

# Will automatically be NULL if it doesn't exist, which is good.
SET @_item_id = (SELECT id FROM wiser_communication WHERE `name` = @_name LIMIT 1);

SELECT IFNULL(@_item_id, 0) AS existingItemId;");
                TemplateQueryStrings.Add("SCHEDULER_FAVORITE_CLEAR", @"SET @user_id = {userId};
SET @favorite_id = {favId};

DELETE FROM schedule_favorites WHERE user_id= @user_id AND favorite_id=@favorite_id LIMIT 1");
                TemplateQueryStrings.Add("SCHEDULER_LOAD_FAVORITES", @"SET @user_id = '{userId}';


SELECT 
	favorite_id AS favoriteId,
    search,
    view_type AS type,
    teacher,
    category,
    location
FROM schedule_favorites WHERE user_id=@user_id;
");
                TemplateQueryStrings.Add("IMPORTEXPORT_GET_ENTITY_NAMES", @"SELECT `name`, module_id AS moduleId
FROM wiser_entity
WHERE `name` <> ''
ORDER BY `name`");
                TemplateQueryStrings.Add("SET_DATA_SELECTOR_REMOVED", @"UPDATE wiser_data_selector
SET removed = 1
WHERE id = {itemId};

SELECT ROW_COUNT() > 0 AS updateSuccessful;");
                TemplateQueryStrings.Add("SET_ORDERING_DISPLAY_NAME", @"SET @_entity_name = {selectedEntityName};
SET @_tab_name = {selectedTabName};
SET @_order = {id};
SET @_display_name = {dislayName};

SET @_tab_name= IF( @_tab_name= ""gegevens"", """", @_tab_name);

UPDATE wiser_entityproperty
SET ordering = @_order
WHERE entity_name = @_entity_name AND tab_name = @_tab_name AND display_name = @_display_name
LIMIT 1");
                TemplateQueryStrings.Add("UPDATE_LINK", @"SET @_linkId = {linkId};
SET @_destinationId = {destinationId};
SET @newOrderNumber = IFNULL((SELECT MAX(ordering) + 1 FROM wiser_itemlink WHERE destination_item_id = @destinationId), 1);
SET @_username = '{username}';
SET @_userId = '{encryptedUserId:decrypt(true)}';

# Update the ordering of all item links that come after the current item link.
UPDATE wiser_itemlink il1
JOIN wiser_itemlink il2 ON il2.destination_item_id = il1.destination_item_id AND il2.ordering > il1.ordering
SET il2.ordering = il2.ordering - 1
WHERE il1.id = @_linkId;

# Update the actual link and add it to the bottom.
UPDATE wiser_itemlink
SET destination_item_id = @destinationId, ordering = @newOrderNumber
WHERE id = @_linkId;");
                TemplateQueryStrings.Add("GET_OPTIONS_FOR_DEPENDENCY", @"SELECT DISTINCT entity_name, IF(tab_name = """", ""Gegevens"", tab_name) as tab_name, display_name, property_name FROM wiser_entityproperty
WHERE entity_name = {entityName}");

                TemplateQueryStrings.Add("GET_AIS_DASHBOARD_OVERVIEW_DATA", @"SET @totalResults = (SELECT COUNT(*) FROM `ais_dashboard` WHERE DATE(started) >= DATE_SUB(CURDATE(), INTERVAL 30 DAY) AND FIND_IN_SET(color, '{color}'));

SELECT 
	id,
    taskname,
    config,
    friendlyname AS friendlyName,
    DATE_FORMAT(started, '%Y-%m-%d') AS startedDate,
    DATE_FORMAT(started, '%H:%i') AS startedTime,
    SUBTIME(TIME(ended), TIME(started)) AS runtime,
    IFNULL(percentage, 0) AS percentageCompleted,
    IFNULL(result, '') AS result,
    IFNULL(groupname, '') AS groupname,
	color, 
    counter,
    IFNULL(debuginformation, '') AS debugInformation,
    0 AS hasChildren,
    @totalResults AS totalResults
FROM `ais_dashboard`
WHERE DATE(started) >= DATE_SUB(CURDATE(), INTERVAL 30 DAY) 
AND FIND_IN_SET(color, '{color}')
ORDER BY startedDate DESC, startedTime DESC
LIMIT {skip}, {take}");
                TemplateQueryStrings.Add("GET_ALL_INPUT_TYPES", @"SELECT DISTINCT inputtype FROM wiser_entityproperty ORDER BY inputtype");
                TemplateQueryStrings.Add("DELETE_ENTITYPROPERTY", @"DELETE FROM wiser_entityproperty WHERE tab_name = '{tabName}' AND entity_name = '{entityName}' AND id = '{entityPropertyId}'");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_ADMIN", @"SELECT id, entity_name, tab_name, display_name, ordering FROM wiser_entityproperty
WHERE tab_name = '{tabName}' AND entity_name = '{entityName}'
ORDER BY ordering ASC");
                TemplateQueryStrings.Add("GET_ENTITY_LIST", @"SELECT name AS id, name FROM wiser_entity
WHERE name != ''
ORDER BY name ASC;");
                TemplateQueryStrings.Add("GET_LANGUAGE_CODES", @"SELECT
    language_code AS text,
    language_code AS `value`
FROM wiser_entityproperty
WHERE
    (entity_name = '{entityName}' OR link_type = '{linkType}')
    AND IF(property_name = '', CreateJsonSafeProperty(display_name), property_name) = '{propertyName}'
    AND language_code <> ''
GROUP BY language_code
ORDER BY language_code");
                TemplateQueryStrings.Add("UPDATE_ORDERING_ENTITY_PROPERTY", @"SET @old_index = {oldIndex} + 1;
SET @new_index = {newIndex} +1;
SET @id = {current_id}; 
SET @entity_name = '{entityName}';
SET @tab_name = '{tabName}';

# move property to given index
UPDATE wiser_entityproperty SET ordering = @new_index WHERE id=@id;

# set other items to given index
UPDATE wiser_entityproperty
	SET ordering = IF(@old_index > @new_index, ordering+1, ordering-1) 
WHERE 
	ordering > IF(@old_index > @new_index, @new_index, @old_index) AND
	ordering < IF(@old_index > @new_index, @old_index, @new_index) AND
	entity_name = @entity_name AND 
	tab_name =  @tab_name AND
	id <> @id;

# update record where index equals the new index value
UPDATE wiser_entityproperty
	SET ordering = IF(@old_index > @new_index, ordering+1, ordering-1) 
WHERE 
	ordering = @new_index AND 
	entity_name = @entity_name AND 
	tab_name =  @tab_name AND
	id <> @id;
");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_TABNAMES", @"SELECT id, IF(tab_name = '', 'Gegevens', tab_name) AS tab_name FROM wiser_entityproperty
WHERE entity_name = '{entityName}'
GROUP BY tab_name
ORDER BY tab_name ASC");
                TemplateQueryStrings.Add("GET_ROLES", @"SELECT id AS id, role_name FROM wiser_roles
WHERE role_name != ''
ORDER BY role_name ASC;");
                TemplateQueryStrings.Add("INSERT_ROLE", @"INSERT INTO `wiser_roles` (`role_name`) VALUES ('{displayName}');");
                TemplateQueryStrings.Add("DELETE_ROLE", @"DELETE FROM `wiser_roles` WHERE id={roleId}");
                TemplateQueryStrings.Add("GET_ITEMLINK_NAMES", @"SELECT DISTINCT type_name AS type_name_text, type_name AS type_name_value FROM `wiser_itemlink` WHERE type_name <> """" AND type_name IS NOT NULL");
                TemplateQueryStrings.Add("DELETE_RIGHT_ASSIGNMENT", @"DELETE FROM `wiser_permission` 
WHERE role_id = {role_id}
	AND entity_property_id = {entity_id}");
                TemplateQueryStrings.Add("UPDATE_ENTITY_PROPERTY_PERMISSIONS", @"INSERT INTO `wiser_permission` (
    role_id, 
    entity_name, 
    item_id, 
    entity_property_id, 
    permissions
) VALUES (
    {role_id},
    '',
    0,
    {entity_id},
    {permission_code}
)
ON DUPLICATE KEY UPDATE permissions = {permission_code}");
                TemplateQueryStrings.Add("GET_GROUPNAME_FOR_SELECTION", @"SELECT DISTINCT group_name FROM `wiser_entityproperty`
WHERE entity_name = {selectedEntityName} AND tab_name = {selectedTabName}");

                TemplateQueryStrings.Add("GET_UNDERLYING_ENTITY_TYPES", @"#SET @_entity_type_list = IF('{entity_types}' LIKE '{%}', '', '{entity_types}');
SET @_entity_name = IF(
    '{entity_name}' NOT LIKE '{%}',
    '{entity_name}',
    # Check for old query string name.
    IF(
        '{entity_types}' NOT LIKE '{%}',
        SUBSTRING_INDEX('{entity_types}', ',', 1),
        ''
    )
);

SELECT inputType, `options`, '' AS acceptedChildTypes
FROM wiser_entityproperty
WHERE entity_name = @_entity_name AND inputtype IN ('item-linker', 'sub-entities-grid')

UNION

SELECT 'sub-entities-grid' AS inputType, '' AS `options`, accepted_childtypes AS acceptedChildTypes
FROM wiser_entity
WHERE name = @_entity_name AND accepted_childtypes <> ''");
                TemplateQueryStrings.Add("GET_PARENT_ENTITY_TYPES", @"#SET @_entity_type_list = IF('{entity_types}' LIKE '{%}', '', '{entity_types}');
SET @_entity_name = IF(
    '{entity_name}' NOT LIKE '{%}',
    '{entity_name}',
    # Check for old query string name.
    IF(
        '{entity_types}' NOT LIKE '{%}',
        SUBSTRING_INDEX('{entity_types}', ',', 1),
        ''
    )
);

SELECT entity_name AS `name`, 'sub-entities-grid' AS inputType, IFNULL(`options`, '') AS `options`
FROM wiser_entityproperty
#WHERE inputtype = 'sub-entities-grid' AND CheckValuesInString(@_entity_name, `options`, '""', '""') = 1
WHERE inputtype = 'sub-entities-grid' AND `options` LIKE CONCAT('%""', @_entity_name, '""%')

UNION

SELECT `name`, 'sub-entities-grid' AS inputType, '' AS `options`
FROM wiser_entity
WHERE FIND_IN_SET(accepted_childtypes, @_entity_name) > 0");
                TemplateQueryStrings.Add("INSERT_NEW_MODULE", @"INSERT INTO `wiser_module` (
	`id`,
    `custom_query`,
    `count_query`
) VALUES (
    {moduleId},
    '',
    ''
);");
                TemplateQueryStrings.Add("CHECK_IF_MODULE_EXISTS", @"SELECT id FROM `wiser_module` WHERE id = {moduleId};");
                TemplateQueryStrings.Add("GET_MODULE_FIELDS", @"SELECT 
    IFNULL(JSON_EXTRACT(`options`, '$.gridViewSettings.columns'), '') AS `fields`
FROM `wiser_module`
WHERE id = {module_id}");
                TemplateQueryStrings.Add("GET_API_ACTION", @"SELECT 
	CASE '{actionType}'
		WHEN 'after_insert' THEN api_after_insert
        WHEN 'after_update' THEN api_after_update
        WHEN 'before_update' THEN api_before_update
        WHEN 'before_delete' THEN api_before_delete
    END AS apiConnectionId_encrypt_withdate
FROM wiser_entity 
WHERE name = '{entityType}';");
                TemplateQueryStrings.Add("UPDATE_API_AUTHENTICATION_DATA", @"UPDATE wiser_api_connection SET authentication_data = '{authenticationData}' WHERE id = {id:decrypt(true)};");
                TemplateQueryStrings.Add("DELETE_MODULE", @"DELETE FROM `wiser_module` WHERE id = {module_id};");
                TemplateQueryStrings.Add("SAVE_MODULE_SETTINGS", @"SET @moduleType := '{module_type}';
SET @moduleOptions := '{options}';

UPDATE `wiser_module` SET 
	custom_query = '{custom_query}',
    count_query = '{count_query}',
    options = NULLIF(@moduleOptions, '')
WHERE id = {module_id};");
                TemplateQueryStrings.Add("INSERT_ENTITYPROPERTY", @"SET @newOrderNr = IFNULL((SELECT MAX(ordering)+1 FROM wiser_entityproperty WHERE entity_name='{entityName}' AND tab_name = '{tabName}'),1);

INSERT INTO wiser_entityproperty(entity_name, tab_name, display_name, property_name, ordering)
VALUES('{entityName}', '{tabName}', '{displayName}', '{propertyName}', @newOrderNr);
#spaties vervangen door underscore");
                TemplateQueryStrings.Add("SEARCH_ITEMS_OLD", @"SET @mid = {moduleid};
SET @parent = '{id:decrypt(true)}';
SET @_entityType = IF('{entityType}' LIKE '{%}', '', '{entityType}');
SET @_searchValue = '{search}';
SET @_searchInTitle = IF('{searchInTitle}' LIKE '{%}' OR '{searchInTitle}' = '1', TRUE, FALSE);
SET @_searchFields = IF('{searchFields}' LIKE '{%}', '', '{searchFields}');
SET @_searchEverywhere = IF('{searchEverywhere}' LIKE '{%}', FALSE, {searchEverywhere});

SELECT 
	i.id,
	i.id AS encryptedId_encrypt_withdate,
	i.title AS name,
	IF(ilc.id IS NULL, 0, 1) AS haschilds,
	we.icon AS spriteCssClass,
	ilp.destination_item_id AS destination_item_id_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
    i.entity_type,
    CreateJsonSafeProperty(id.`key`) AS property_name,
    id.`value` AS property_value,
    ilp.type_name AS link_type
FROM wiser_item i
LEFT JOIN wiser_itemlink ilp ON ilp.destination_item_id = @parent AND ilp.item_id = i.id
LEFT JOIN wiser_entityproperty p ON p.entity_name = i.entity_type
LEFT JOIN wiser_itemdetail id ON id.item_id = i.id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND id.`key` = p.property_name) OR ((p.property_name IS NULL OR p.property_name = '') AND id.`key` = p.display_name))
LEFT JOIN wiser_itemlink ilc ON ilc.destination_item_id = i.id
LEFT JOIN wiser_entity we ON we.name = i.entity_type
WHERE i.removed = 0
AND i.entity_type = @_entityType
AND (@_searchEverywhere = TRUE OR ilp.id IS NOT NULL)
AND (
    (NOT @_searchInTitle AND @_searchFields = '')
    OR (@_searchInTitle AND i.title LIKE CONCAT('%', @_searchValue, '%'))
    OR (@_searchFields <> '' AND FIND_IN_SET(id.key, @_searchFields) AND id.value LIKE CONCAT('%', @_searchValue, '%'))
)

GROUP BY i.id, id.id
ORDER BY ilp.ordering, i.title
#LIMIT {skip}, {take}");
                TemplateQueryStrings.Add("PUBLISH_LIVE", @"UPDATE wiser_item SET published_environment=4 WHERE id={itemid:decrypt(true)};");
                TemplateQueryStrings.Add("PUBLISH_ITEM", @"UPDATE wiser_item SET published_environment=4 WHERE id={itemid:decrypt(true)};");
                TemplateQueryStrings.Add("HIDE_ITEM", @"UPDATE wiser_item SET published_environment=0 WHERE id={itemid:decrypt(true)};");
                TemplateQueryStrings.Add("RENAME_ITEM", @"SET @item_id={itemid:decrypt(true)};
SET @newname='{name}';

UPDATE wiser_item SET title=@newname WHERE id=@item_id LIMIT 1;");
                TemplateQueryStrings.Add("LOAD_USER_SETTING", @"SET @user_id = '{encryptedUserId:decrypt(true)}';
SET @setting_name = '{settingName}';
SET @entity_type = 'wiser_user_settings';

SELECT CONCAT(`value`, long_value) AS `value`
FROM wiser_itemdetail detail
	JOIN wiser_item item ON item.id=detail.item_id AND item.unique_uuid = @user_id AND item.entity_type=@entity_type
WHERE detail.`key` = @setting_name ");
                TemplateQueryStrings.Add("GET_SAVED_DATA_SELECTORS", @"SELECT id, `name`
FROM wiser_data_selector
WHERE `name` <> '' AND removed = 0 AND request_json <> '' AND request_json <> '{}'
ORDER BY `name`");
                TemplateQueryStrings.Add("UPDATE_FILE_TITLE", @"UPDATE wiser_itemfile SET title = '{value}' WHERE id = {fileId} AND item_id = {itemId:decrypt(true)};");
                TemplateQueryStrings.Add("UPDATE_FILE_NAME", @"UPDATE wiser_itemfile SET file_name = '{value}' WHERE id = {fileId} AND item_id = {itemId:decrypt(true)};");
                TemplateQueryStrings.Add("GET_UNDERLYING_LINKED_TYPES", @"SET @_entity_name = IF(
    '{entity_name}' NOT LIKE '{%}',
    '{entity_name}',
    # Check for old query string name. Takes the first item in a comma-separated list of entity type names.
    IF(
        '{entity_types}' NOT LIKE '{%}',
        SUBSTRING_INDEX('{entity_types}', ',', 1),
        ''
    )
);

SELECT connected_entity_type AS entityType, type AS linkTypeNumber, `name` AS linkTypeName
FROM wiser_link
WHERE destination_entity_type = @_entity_name AND show_in_data_selector = 1
ORDER BY entityType");
                TemplateQueryStrings.Add("GET_PARENT_LINKED_TYPES", @"SET @_entity_name = IF(
    '{entity_name}' NOT LIKE '{%}',
    '{entity_name}',
    # Check for old query string name. Takes the first item in a comma-separated list of entity type names.
    IF(
        '{entity_types}' NOT LIKE '{%}',
        SUBSTRING_INDEX('{entity_types}', ',', 1),
        ''
    )
);

SELECT destination_entity_type AS entityType, type AS linkTypeNumber, `name` AS linkTypeName
FROM wiser_link
WHERE connected_entity_type = @_entity_name AND show_in_data_selector = 1
ORDER BY entityType");
                TemplateQueryStrings.Add("SAVE_USER_SETTING", @"SET @user_id = '{encryptedUserId:decrypt(true)}';
SET @setting_name = '{settingName}';
SET @setting_value = '{settingValue}';
SET @entity_type = 'wiser_user_settings';
SET @title = 'Wiser user settings';

SET @itemId = (SELECT id FROM wiser_item item WHERE item.unique_uuid = @user_id AND item.entity_type=@entity_type AND @setting_name <> ''AND @user_id <> '' AND @user_id NOT LIKE '{%}');

# make sure the wiser item exists
INSERT IGNORE INTO wiser_item (id, unique_uuid, entity_type, title)
	VALUES(@itemId, @user_id, @entity_type, @title);

# now update the correct value
INSERT INTO wiser_itemdetail (item_id, `key`, `value`, `long_value`)
	SELECT 
		item.id,
		@setting_name,
		IF(LENGTH(@setting_value > 1000), '', @setting_value),
		IF(LENGTH(@setting_value >= 1000), @setting_value, null)
	FROM wiser_item item WHERE item.unique_uuid = @user_id AND item.entity_type=@entity_type
ON DUPLICATE KEY UPDATE 
	`value` = IF(LENGTH(@setting_value > 1000), '', @setting_value), 
	`long_value` = IF(LENGTH(@setting_value >= 1000), @setting_value, null);
    
SELECT @setting_value;");
                TemplateQueryStrings.Add("GET_ITEM_VALUE", @"SELECT
	id,
    `key`,
    IF(long_value IS NULL OR long_value = '', `value`, long_value) AS `value`
FROM wiser_itemdetail
WHERE item_id = {itemId:decrypt(true)}
AND `key` = '{propertyName}'");
                TemplateQueryStrings.Add("GET_ENTITY_TYPE", @"SET @_entityType = '{entityType}';
SET @_moduleId = IF('{moduleId}' LIKE '{%}', '', '{moduleId}');

SELECT 
	id,
    name,
    module_id,
    accepted_childtypes,
    icon,
    icon_add,
    show_in_tree_view,
    show_overview_tab,
    show_title_field
FROM wiser_entity
WHERE name = @_entityType
AND (@_moduleId = '' OR module_id = @_moduleId)");
                TemplateQueryStrings.Add("GET_ITEM_DETAILS", @"SET @_itemId = {itemId:decrypt(true)};
SET @userId = {encryptedUserId:decrypt(true)};

SELECT 
	i.id, 
	i.id AS encryptedId_encrypt_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
	i.title, 
	i.entity_type, 
	IFNULL(p.property_name, p.display_name) AS property_name,
	CONCAT(IFNULL(id.`value`, ''), IFNULL(id.`long_value`, ''), IFNULL(CONCAT('[', GROUP_CONCAT(DISTINCT CONCAT('{ ""itemId"": ', wif.item_id, ', ""fileId"": ', wif.id, ', ""name"": ""', wif.file_name, '"", ""title"": ""', wif.title, '"", ""extension"": ""', wif.extension, '"", ""size"": ', IFNULL(OCTET_LENGTH(wif.content), 0), ' }')), ']'), '')) AS property_value
FROM wiser_item i
LEFT JOIN wiser_entityproperty p ON p.entity_name = i.entity_type
LEFT JOIN wiser_itemdetail id ON id.item_id = i.id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND id.`key` = p.property_name) OR ((p.property_name IS NULL OR p.property_name = '') AND id.`key` = p.display_name))
LEFT JOIN wiser_itemfile wif ON wif.item_id = i.id AND wif.property_name = IFNULL(p.property_name, p.display_name)

# Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id

WHERE i.id = @_itemId
AND (permission.id IS NULL OR (permission.permissions & 1) > 0)
GROUP BY i.id, p.id");
                TemplateQueryStrings.Add("GET_TITLE", @"SET @_itemId = {itemId:decrypt(true)};
SELECT title FROM wiser_item WHERE id = @_itemId;");
                TemplateQueryStrings.Add("GET_PROPERTIES_OF_ENTITY", @"SELECT
	IF(tab_name = '', 'Gegevens', tab_name) AS tab_name,
    display_name,
    IF(property_name = '', display_name, property_name) AS property_name
FROM wiser_entityproperty
WHERE entity_name = '{entityType}'
AND inputtype NOT IN ('file-upload', 'grid', 'image-upload', 'sub-entities-grid', 'item-linker', 'linked-item', 'action-button')

UNION SELECT 'Algemeen' AS tab_name, 'ID' AS display_name, 'id' AS property_name
UNION SELECT 'Algemeen' AS tab_name, 'UUID' AS display_name, 'unique_uuid' AS property_name
UNION SELECT 'Algemeen' AS tab_name, 'Toegevoegd door' AS display_name, 'added_by' AS property_name
UNION SELECT 'Algemeen' AS tab_name, 'Gewijzigd door' AS display_name, 'changed_by' AS property_name
UNION SELECT 'Algemeen' AS tab_name, 'Naam' AS display_name, 'title' AS property_name

ORDER BY tab_name ASC, display_name ASC");
                TemplateQueryStrings.Add("GET_ALL_MODULES_INFORMATION", @"SELECT 
	id,
	custom_query,
	count_query,
	`options`,
	IF(`options` IS NULL, 0, 1) AS `isGridview`,    
    IF(`options` IS NULL, 'treeview', 'gridview') AS `type`,
# we check for type and isValidJson
    IF(JSON_VALID(`options`) AND `options` IS NOT NULL AND `options` <> '', 1, 0) AS `isValidJson`,
    #adding extra JSON_VALID to prevent errors in the query result.
	IF(JSON_VALID(`options`),IFNULL(JSON_EXTRACT(`options`, '$.gridViewSettings.pageSize'), ''), '') AS `pageSize`,
	IF(JSON_VALID(`options`),IF(JSON_EXTRACT(`options`, '$.gridViewSettings.toolbar.hideCreateButton') = true, 1, 0), 0) AS `hideCreationButton`,
	IF(JSON_VALID(`options`),IF(JSON_EXTRACT(`options`, '$.gridViewSettings.hideCommandColumn') = true, 1, 0), 0) AS `hideCommandButton`
FROM `wiser_module` 
");

                TemplateQueryStrings.Add("GET_AVAILABLE_ENTITY_TYPES", @"SELECT DISTINCT(e2.name)
FROM wiser_entity e
LEFT JOIN wiser_item i ON i.entity_type = e.name AND i.moduleid = e.module_id
JOIN wiser_entity e2 ON e2.module_id = {moduleId} AND e2.name <> '' AND FIND_IN_SET(e2.name, e.accepted_childtypes)
WHERE e.module_id = {moduleId}
AND (({parentId:decrypt(true)} = 0 AND e.name = '') OR ({parentId:decrypt(true)} > 0 AND i.id = {parentId:decrypt(true)}))");

                TemplateQueryStrings.Add("IMPORTEXPORT_GET_LINK_TYPES", @"SELECT type AS id, `name`
FROM wiser_link
ORDER BY `name`");
                TemplateQueryStrings.Add("SAVE_INITIAL_VALUES", @"SET @_entity_name = '{entity_name}';
SET @_tab_name = '{tab_name}';
SET @_tab_name = IF( @_tab_name='gegevens', '', @_tab_name);
SET @_display_name = '{display_name}';
SET @_property_name = IF('{property_name}' = '', @_display_name, '{property_name}');
SET @_overviewvisibility = {visible_in_overview};
SET @_overviewvisibility = IF(@_overviewvisibility = TRUE OR @_overviewvisibility = 'true', 1, 0);
SET @_overviewType = '{overview_fieldtype}';
SET @_overviewWidth = '{overview_width}';
SET @_groupName = '{group_name}';
SET @_input_type = '{inputtype}';
SET @_explanation = '{explanation}';
SET @_mandatory = '{mandatory}';
SET @_mandatory = IF(@_mandatory = TRUE OR @_mandatory = 'true', 1, 0);
SET @_readOnly = '{readonly}';
SET @_readOnly = IF(@_readOnly = TRUE OR @_readOnly = 'true', 1, 0);
SET @_seo = '{also_save_seo_value}';
SET @_seo = IF(@_seo = TRUE OR @_seo = 'true', 1, 0);
SET @_width = {width};
SET @_height = {height};
SET @_langCode = '{language_code}';
SET @_dependsOnField = '{depends_on_field}';
SET @_dependsOnOperator = IF('{depends_on_operator}' = '', NULL, '{depends_on_operator}');
SET @_dependsOnValue = '{depends_on_value}';
SET @_css = '{css}';
SET @_regexValidation = '{regex_validation}';
SET @_defaultValue = '{default_value}';
SET @_automation = '{automation}';
SET @_customScript = '{custom_script}';
SET @_options = '{options}';
SET @_data_query = '{data_query}';
SET @_grid_delete_query = '{grid_delete_query}';
SET @_grid_insert_query = '{grid_insert_query}';
SET @_grid_update_query = '{grid_update_query}';

SET @_id = {id};

UPDATE wiser_entityproperty
SET 
inputtype = @_input_type,
display_name = @_display_name,
property_name = @_property_name,
visible_in_overview= @_overviewvisibility,
overview_fieldtype= @_overviewType,
overview_width= @_overviewWidth,
group_name= @_groupName,
explanation= @_explanation,
regex_validation= @_regexValidation,
mandatory= @_mandatory,
readonly= @_readOnly,
default_value= @_defaultValue,
automation= @_automation,
css= @_css,
width= @_width,
height= @_height,
depends_on_field= @_dependsOnField,
depends_on_operator= @_dependsOnOperator,
depends_on_value= @_dependsOnValue,
language_code= @_langCode,
custom_script= @_customScript,
also_save_seo_value = @_seo,
tab_name = @_tab_name,
options = @_options,
data_query = @_data_query,
grid_delete_query = @_grid_delete_query, 
grid_insert_query= @_grid_insert_query,
grid_update_query = @_grid_update_query
WHERE entity_name = @_entity_name AND id = @_id
LIMIT 1; ");


                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_FOR_SELECTED", @"SELECT
id,
display_name,
inputtype, 
visible_in_overview, 
overview_width, 
overview_fieldtype, 
mandatory, 
readonly, 
also_save_seo_value,
width,
height,
IF(tab_name = '', 'Gegevens', tab_name) AS tab_name,
group_name,
property_name,
explanation,
default_value,
automation,
options,
depends_on_field,
depends_on_operator,
depends_on_value,
IFNULL(data_query,"""") AS data_query,
IFNULL(grid_delete_query,"""") AS grid_delete_query,
IFNULL(grid_update_query,"""") AS grid_update_query,
IFNULL(grid_insert_query,"""") AS grid_insert_query,
regex_validation,
IFNULL(css, '') AS css,
language_code,
IFNULL(custom_script, '') AS custom_script,
(SELECT COUNT(1) FROM wiser_itemdetail INNER JOIN wiser_item ON wiser_itemdetail.item_id = wiser_item.id WHERE wiser_itemdetail.key = wiser_entityproperty.property_name AND wiser_item.entity_type = wiser_entityproperty.entity_name) > 0 as field_in_use
FROM wiser_entityproperty
WHERE id = {id} AND entity_name = '{entityName}'
");
                TemplateQueryStrings.Add("MOVE_ITEM", @"#Item verplaatsen naar ander item
SET @src_id = '{source:decrypt(true)}';
SET @dest_id = '{destination:decrypt(true)}';
SET @location = '{position}'; #can be over after or before
SET @srcparent = '{source_parent:decrypt(true)}'; #this must come from client because items can have multiple parents
SET @destparent = '{dest_parent:decrypt(true)}'; #this must come from client because items can have multiple parents
SET @oldordering = (SELECT ordering FROM wiser_itemlink WHERE item_id=@src_id AND destination_item_id=@srcparent LIMIT 1);
#SET @ordernumbernewitem = (SELECT ordering FROM wiser_itemlink WHERE item_id@dest_id AND destination_item_id=@destparent LIMIT 1);
SET @newordernumbernewfolder = (SELECT max(ordering)+1 FROM wiser_itemlink WHERE destination_item_id=IF(@location = 'over', @dest_id, @destparent));
SET @newordernumbernewfolder = IFNULL(@newordernumbernewfolder,1);
SET @newordernumber = (SELECT ordering FROM wiser_itemlink WHERE item_id=@dest_id AND destination_item_id=@destparent LIMIT 1);
SET @sourceType = (SELECT entity_type FROM wiser_item WHERE id = @src_id);
SET @destinationType = (SELECT entity_type FROM wiser_item WHERE id = IF(@location = 'over', @dest_id, @destparent));
SET @destinationAcceptedChildTypes = (
	SELECT GROUP_CONCAT(e.accepted_childtypes)
	FROM wiser_entity AS e
	LEFT JOIN wiser_item AS i ON i.entity_type = e.name AND i.id = IF(@location = 'over', @dest_id, @destparent)
	WHERE i.id IS NOT NULL
	OR (@location <> 'over' AND @destparent = '0' AND e.name = '')
	OR (@location = 'over' AND @dest_id = '0' AND e.name = '')
);

#Items voor of na de plaatsing (before/after) 1 plek naar achteren schuiven (niet bij plaatsen op een ander item, want dan komt het nieuwe item altijd achteraan)
UPDATE wiser_itemlink
SET
  ordering=ordering+1
WHERE destination_item_id=@destparent
AND ordering>=IF(@location='before',@newordernumber,@newordernumber+1) #als het before is dan alles ophogen vanaf, bij after alles erna
AND item_id<>@src_id
AND (@location='before' OR @location='after')
AND destination_item_id=@srcparent
AND FIND_IN_SET(@sourceType, @destinationAcceptedChildTypes);

#Node plaatsen op nieuwe plek
UPDATE wiser_itemlink 
SET
  destination_item_id=IF(@location='over',@dest_id,@destparent), #bij 'over' de ID wordt de parent, bij before/after wordt de parent van de nieuwe node de nieuwe parent
	ordering=IF(@location='before',@newordernumber,IF(@location='after',@newordernumber+1,@newordernumbernewfolder)) #als het before is dan op die plek zetten anders 1 hoger
WHERE item_id=@src_id
AND destination_item_id=@srcparent
AND FIND_IN_SET(@sourceType, @destinationAcceptedChildTypes);

#In oude map gat opvullen (items opschuiven naar voren)
UPDATE wiser_itemlink
SET
  ordering=ordering-1
WHERE destination_item_id=@srcparent
AND ordering>@oldordering
AND FIND_IN_SET(@sourceType, @destinationAcceptedChildTypes);

SELECT 
	@isexpanded, 
    @location, 
    @newordernumber, 
    @destparent, 
	CASE WHEN NOT FIND_IN_SET(@sourceType, @destinationAcceptedChildTypes)
        THEN (SELECT CONCAT('Items van type ""', @sourceType, '"" mogen niet toegevoegd worden onder items van type ""', @destinationType, '"".') AS error)
        ELSE ''
    END AS error;");
                TemplateQueryStrings.Add("GET_CONTEXT_MENU", @"SET @itemid = {item_id:decrypt(true)};
SET @moduleid = {module_id};
SET @entity_type = (SELECT entity_type FROM wiser_item WHERE id=@itemid);
SET @itemname = (SELECT title FROM wiser_item WHERE id=@itemid);
SET @userId = {encryptedUserId:decrypt(true)};

SELECT 
	CONCAT('\'', @itemname, '\' hernoemen') AS text, 
    'icon-album-rename' AS spriteCssClass,
    'RENAME_ITEM' AS attraction, 
    '' AS attrentity_type
    #the JSON must consist of a subnode with attributes, so attr is the name of the json object containing 'action' as a value, herefore the name here is attr...action
    FROM wiser_item i 
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = i.moduleid
    
    WHERE i.id = @itemid 
    AND i.readonly = 0
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 4) > 0)
			OR 
			(permission.permissions & 4) > 0
		)
    
UNION
    SELECT CONCAT('Nieuw(e) \'', i.name, '\' aanmaken'), 
	i.icon_add,'CREATE_ITEM', 
	i.name
    FROM wiser_entity i
    JOIN wiser_entity we ON we.module_id=@moduleid AND we.name=@entity_type
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = @itemid
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = @moduleid
    
    WHERE i.module_id = @moduleid
    AND i.`name` IN (we.accepted_childtypes) AND i.name <> ''
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 2) > 0)
			OR 
			(permission.permissions & 2) > 0
		)

UNION
	SELECT CONCAT('\'', @itemname, '\' dupliceren') AS text, 
	'icon-document-duplicate',
	'DUPLICATE_ITEM',
	'' 
    FROM wiser_item i 
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = i.moduleid
    
    WHERE i.id = @itemid 
    AND i.readonly = 0
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 2) > 0)
			OR 
			(permission.permissions & 2) > 0
		)

UNION
	SELECT CONCAT('Publiceer naar live'),
	'icon-globe',
	'PUBLISH_LIVE',
	'' 
    FROM wiser_item i 
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = i.moduleid
    
    WHERE i.id=@itemid 
    AND i.published_environment <> 4
    AND i.readonly = 0
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 4) > 0)
			OR 
			(permission.permissions & 4) > 0
		)
    
UNION
	SELECT CONCAT('\'', @itemname, '\' tonen') AS text, 
	'item-light-on',
	'PUBLISH_ITEM',
	'' 
    FROM wiser_item i 
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = i.moduleid
    
    WHERE i.id = @itemid 
    AND i.published_environment = 0
    AND i.readonly = 0
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 4) > 0)
			OR 
			(permission.permissions & 4) > 0
		)
    
UNION
	SELECT CONCAT('\'', @itemname, '\' verbergen') AS text, 
	'icon-light-off',
	'HIDE_ITEM',
	'' 
    FROM wiser_item i 
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = i.moduleid
    
    WHERE i.id = @itemid 
    AND i.published_environment > 0
    AND i.readonly = 0
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 4) > 0)
			OR 
			(permission.permissions & 4) > 0
		)
    
UNION
	SELECT CONCAT('\'', @itemname, '\' verwijderen') AS text, 
	'icon-delete',
	'REMOVE_ITEM',
	''
    FROM wiser_item i 
    
    # Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
	LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
	LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id
	LEFT JOIN wiser_permission permissionModule ON permissionModule.role_id = user_role.role_id AND permissionModule.module_id = i.moduleid
    
    WHERE i.id = @itemid 
    AND i.readonly = 0
	AND (
			(permissionModule.id IS NULL AND permission.id IS NULL)
			OR
			(permission.id IS NULL AND (permissionModule.permissions & 8) > 0)
			OR 
			(permission.permissions & 8) > 0
		)");
                TemplateQueryStrings.Add("GET_ITEMLINK_NUMBERS", @"SELECT 'Hoofdkoppeling' AS type_text, 1 AS type_value 
UNION ALL
SELECT 'Subkoppeling' AS type_text, 2 AS type_value 
UNION ALL
SELECT 'Automatisch gegeneerd' AS type_text, 3 AS type_value 
UNION ALL
SELECT 'Media' AS type_text, 4 AS type_value 
UNION ALL
SELECT DISTINCT type AS type_text, type AS type_value FROM `wiser_itemlink` WHERE type > 100");
                TemplateQueryStrings.Add("REMOVE_LINK", @"SET @sourceId = IF('{source_plain}' LIKE '{%}', '{source:decrypt(true)}', '{source_plain}');
SET @destinationId = {destination:decrypt(true)};
SET @_linkTypeNumber = IF('{linkTypeNumber}' LIKE '{%}' OR '{linkTypeNumber}' = '', '2', '{linkTypeNumber}');
SET @orderNumber = IFNULL((SELECT MIN(ordering) FROM wiser_itemlink WHERE item_id = @sourceId AND destination_item_id = @destinationId AND type = @_linkTypeNumber), -1);
SET @_username = '{username}';
SET @_userId = '{encryptedUserId:decrypt(true)}';

DELETE FROM wiser_itemlink WHERE item_id = @sourceId AND destination_item_id = @destinationId AND type = @_linkTypeNumber;

UPDATE wiser_itemlink SET ordering = ordering - 1 WHERE @orderNumber > -1 AND ordering > @orderNumber AND destination_item_id = @destinationId AND type = @_linkTypeNumber;");
                TemplateQueryStrings.Add("GET_COLUMNS_FOR_FIELD_TABLE", @"#Verkrijg de kolommen die getoond moeten worden bij een specifiek soort entiteit
SET @entitytype = '{entity_type}';
SET @_linkType = '{linkType}';

SELECT  
	CONCAT('property_.', CreateJsonSafeProperty(LOWER(IF(p.property_name IS NULL OR p.property_name = '', p.display_name, p.property_name)))) AS field,
    p.display_name AS title,
    p.overview_fieldtype AS fieldType,
    p.overview_width AS width
FROM wiser_entityproperty p 
WHERE (p.entity_name = @entitytype OR (p.link_type > 0 AND p.link_type = @_linkType))
AND p.visible_in_overview = 1
GROUP BY IF(p.property_name IS NULL OR p.property_name = '', p.display_name, p.property_name)
ORDER BY p.ordering;");
                TemplateQueryStrings.Add("GET_COLUMNS_FOR_LINK_TABLE", @"SET @destinationId = {id:decrypt(true)};
SET @_linkTypeNumber = IF('{linkTypeNumber}' LIKE '{%}' OR '{linkTypeNumber}' = '', '2', '{linkTypeNumber}');

SELECT 
	CONCAT('property_.', CreateJsonSafeProperty(IF(p.property_name IS NULL OR p.property_name = '', p.display_name, p.property_name))) AS field,
    p.display_name AS title,
    p.overview_fieldtype AS fieldType,
    p.overview_width AS width
FROM wiser_entityproperty p 
JOIN wiser_item i ON i.entity_type = p.entity_name
JOIN wiser_itemlink il ON il.item_id = i.id AND il.destination_item_id = @destinationId AND il.type = @_linkTypeNumber
WHERE p.visible_in_overview = 1
GROUP BY IF(p.property_name IS NULL OR p.property_name = '', p.display_name, p.property_name)
ORDER BY p.ordering;");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_BY_LINK", @"SET @_link_type = IF('{linktype}' LIKE '{%}', '', '{linktype}');
SET @_entity_type = IF('{entitytype}' LIKE '{%}', '', '{entitytype}');

SELECT
    CONCAT_WS(' - ', wip.entity_name, NULLIF(wip.tab_name, ''), NULLIF(wip.group_name, ''), wip.display_name) AS `text`,
    IF(property_name = '', CreateJsonSafeProperty(display_name), property_name) AS `value`
FROM wiser_itemlink wil
JOIN wiser_entityproperty wip ON wip.entity_name = wil.entity_name
WHERE wil.type = @_link_type
# Some entities should be ignored due to their input types.
AND wip.inputtype NOT IN ('sub-entities-grid', 'item-linker', 'linked-item', 'auto-increment', 'file-upload', 'action-button')
GROUP BY wip.id");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_LINKED_TO_UP", @"# Bovenliggende objecten.

SET @_module_list = IF('{modules}' LIKE '{%}', '', '{modules}');
SET @_entity_type_list = IF('{entity_types}' LIKE '{%}', '', '{entity_types}');

SELECT *
FROM (
    SELECT
        entity_name AS `name`,
        inputtype AS inputType,
        entity_name AS type,
        IF(
            inputtype = 'item-linker',
            (SELECT GROUP_CONCAT(DISTINCT entity_name) FROM wiser_itemlink WHERE type = `options`->>'$.linkTypeNumber'),
            entity_name
        ) AS entityTypes,
        IF(inputtype = 'item-linker', module_id, 0) AS moduleId
    FROM wiser_entityproperty
    WHERE
        IF(@_module_list = '', 1 = 1, FIND_IN_SET(module_id, @_module_list) > 0)
        AND inputtype IN ('item-linker', 'sub-entities-grid')
        AND IF(
            inputtype = 'item-linker',
            JSON_UNQUOTE(JSON_EXTRACT(`options`, JSON_UNQUOTE(JSON_SEARCH(`options`, 'one', @_entity_type_list)))) IS NOT NULL,
            FIND_IN_SET(`options`->>'$.entityType', @_entity_type_list) > 0
        )

    UNION

    SELECT
        wep.entity_name AS `name`,
        wep.inputtype AS inputType,
        wep.entity_name AS type,
        IF(
            wep.inputtype = 'item-linker',
            (SELECT GROUP_CONCAT(DISTINCT entity_name) FROM wiser_itemlink WHERE type = wep.`options`->>'$.linkTypeNumber'),
            entity_name
        ) AS entityTypes,
        IF(inputtype = 'item-linker', wep.module_id, 0) AS moduleId
    FROM wiser_entity we
	JOIN wiser_entityproperty wep ON wep.entity_name = we.`name` AND wep.inputtype IN ('item-linker', 'sub-entities-grid')
    WHERE
        IF(@_module_list = '', 1 = 1, FIND_IN_SET(we.module_id, @_module_list) > 0)
        AND CompareLists(@_entity_type_list, we.accepted_childtypes)
) t
GROUP BY t.type
ORDER BY t.type");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES_LINKED_TO_DOWN", @"# Onderliggende objecten.

SET @_module_list = IF('{modules}' LIKE '{%}', '', '{modules}');
SET @_entity_type_list = IF('{entity_types}' LIKE '{%}', '', '{entity_types}');

SELECT *
FROM (
    SELECT
        #display_name AS `name`,
        CAST(IF(inputtype = 'item-linker', `options`->>'$.linkTypeNumber', `options`->>'$.entityType') AS CHAR) AS `name`,
        inputtype AS inputType,
        CAST(IF(inputtype = 'item-linker', `options`->>'$.linkTypeNumber', `options`->>'$.entityType') AS CHAR) AS type,
        IF(
            inputtype = 'item-linker',
            IF(
                `options`->>'$.entityTypes' IS NULL,
                `options`->>'$.linkTypeNumber',
                REPLACE(REPLACE(REPLACE(REPLACE(`options`->> '$.entityTypes', '[', ''), ']', ''), '""', '' ), ', ', ',')
            ),
            `options`->>'$.entityType'
        ) AS entityTypes,
        IF(inputtype = 'item-linker', `options`->>'$.moduleId', 0) AS moduleId
    FROM wiser_entityproperty
    WHERE
        IF(@_module_list = '', 1 = 1, FIND_IN_SET(module_id, @_module_list) > 0)
        AND FIND_IN_SET(entity_name, @_entity_type_list) > 0
        AND inputtype IN ('item-linker', 'sub-entities-grid')

    UNION

    SELECT
        #wep.display_name AS `name`,
        CAST(IF(wep.inputtype = 'item-linker', wep.`options`->>'$.linkTypeNumber', wep.`options`->>'$.entityType') AS CHAR) AS `name`,
        wep.inputtype AS inputType,
        CAST(IF(wep.inputtype = 'item-linker', wep.`options`->>'$.linkTypeNumber', wep.`options`->>'$.entityType') AS CHAR) AS type,
        IF(
            wep.inputtype = 'item-linker',
            IF(
                wep.`options`->>'$.entityTypes' IS NULL,
                wep.`options`->>'$.linkTypeNumber',
                REPLACE(REPLACE(REPLACE(REPLACE(wep.`options`->> '$.entityTypes', '[', ''), ']', ''), '""', '' ), ', ', ',')
            ),
            wep.`options`->>'$.entityType'
        ) AS entityTypes,
        IF(wep.inputtype = 'item-linker', wep.`options`->>'$.moduleId', 0) AS moduleId
    FROM wiser_entity we
    JOIN wiser_entityproperty wep ON wep.inputtype IN ('item-linker', 'sub-entities-grid')
    WHERE
        FIND_IN_SET(wep.entity_name, we.accepted_childtypes) > 0
        AND IF(@_module_list = '', 1 = 1, FIND_IN_SET(wep.module_id, @_module_list) > 0)
) t
GROUP BY t.type
ORDER BY t.type");

                TemplateQueryStrings.Add("GET_MODULES", @"SELECT id, name as module_name
FROM wiser_module
ORDER BY name ASC;
");
                TemplateQueryStrings.Add("GET_MODULE_ROLES", @"
SELECT
	permission.id AS `permission_id`,
	role.id AS `role_id`,
	role.role_name,
	module.id AS `module_id`,
	module.name AS module_name
FROM wiser_roles AS role
LEFT JOIN wiser_permission AS permission ON role.id = permission.role_id
LEFT JOIN wiser_module AS module ON permission.module_id = module.id
WHERE role.id = {role_id}");
                TemplateQueryStrings.Add("DELETE_MODULE_RIGHT_ASSIGNMENT", @"DELETE FROM `wiser_system`.`wiser_permission`
WHERE role_id = {role_id} AND module_id={module_id}");

                TemplateQueryStrings.Add("IMPORTEXPORT_GET_ENTITY_PROPERTIES", @"SELECT property.`name`, property.`value`, property.languageCode, property.isImageField, property.allowMultipleImages
FROM (
    SELECT 'Item naam' AS `name`, 'itemTitle' AS `value`, '' AS languageCode, 0 AS isImageField, 0 AS allowMultipleImages, 0 AS baseOrder
    FROM DUAL
    WHERE '{entityName}' NOT LIKE '{%}' AND '{entityName}' <> ''
    UNION
    SELECT
        CONCAT(
            IF(display_name = '', property_name, display_name),
            IF(
                language_code <> '',
                CONCAT(' (', language_code, ')'),
                ''
            )
        ) AS `name`,
        IF(property_name = '', display_name, property_name) AS `value`,
        language_code AS languageCode,
        inputtype = 'image-upload' AS isImageField,
        IFNULL(JSON_UNQUOTE(JSON_EXTRACT(NULLIF(`options`, ''), '$.multiple')), 'true') = 'true' AS allowMultipleImages,
        1 AS baseOrder
    FROM wiser_entityproperty
    WHERE entity_name = '{entityName}'
    OR ('{linkType}' > 0 AND link_type = '{linkType}')
    ORDER BY baseOrder, `name`
) AS property");
                TemplateQueryStrings.Add("GET_ROLE_RIGHTS", @"SELECT
	properties.id AS `property_id`,
	properties.entity_name,
	properties.display_name,
	IFNULL(permissions.permissions, 15) AS `permission`,
    {role_id} AS `role_id`
FROM `wiser_entityproperty` AS properties
LEFT JOIN `wiser_permission` AS permissions ON permissions.entity_property_id = properties.id AND permissions.role_id = {role_id}
WHERE NULLIF(properties.display_name, '') IS NOT NULL
	AND NULLIF(properties.entity_name, '') IS NOT NULL
GROUP BY properties.id
ORDER BY properties.entity_name, properties.display_name");
                TemplateQueryStrings.Add("GET_MODULE_PERMISSIONS", @"SELECT
	role.id AS `role_id`,
	role.role_name,
	module.id AS `module_id`,
	IFNULL(module.name, CONCAT('ModuleID: ',module.id)) AS module_name,
	IFNULL(permission.permissions, 15) AS `permission`
FROM wiser_module AS module
JOIN wiser_roles AS role ON role.id = {role_id}
LEFT JOIN wiser_permission AS permission ON role.id = permission.role_id AND permission.module_id = module.id
ORDER BY module_name ASC
");
                TemplateQueryStrings.Add("UPDATE_MODULE_PERMISSION", @" INSERT INTO `wiser_permission` (
     `role_id`,
     `entity_name`,
     `item_id`,
     `entity_property_id`,
     `permissions`,
     `module_id`
 ) 
 VALUES (
     {role_id}, 
     '',
     0,
     0,
     {permission_code},
     {module_id}
 )
ON DUPLICATE KEY UPDATE permissions = {permission_code};");

                TemplateQueryStrings.Add("GET_DATA_SELECTOR_BY_ID", @"SET @_id = {id};

SELECT id, `name`, module_selection AS modules, request_json AS requestJson, saved_json AS savedJson, show_in_export_module AS showInExportModule
FROM wiser_data_selector
WHERE id = @_id");
                TemplateQueryStrings.Add("SAVE_DATA_SELECTOR", @"SET @_name = '{name}';
SET @_modules = '{modules}';
SET @_request_json = '{requestJson}';
SET @_saved_json = IF('{savedJson}' = CONCAT('{', 'savedJson', '}'), '', '{savedJson}');

# Will automatically be NULL if it doesn't exist, which is good.
SET @_item_id = (SELECT id FROM wiser_data_selector WHERE `name` = @_name);

# Whether the data selector will be available in the export module.
SET @_show_in_export_module = IF(
    '{showInExportModule}' LIKE '{%}' AND @_item_id IS NOT NULL AND @_item_id > 0,
    (SELECT show_in_export_module FROM wiser_data_selector WHERE id = @_item_id),
    '{showInExportModule}' = '1'
);

INSERT INTO wiser_data_selector (id, `name`, module_selection, request_json, saved_json, changed_on, show_in_export_module)
VALUES (@_item_id, @_name, @_modules, @_request_json, @_saved_json, NOW(), @_show_in_export_module)
ON DUPLICATE KEY UPDATE module_selection = VALUES(module_selection), request_json = VALUES(request_json), saved_json = VALUES(saved_json), changed_on = VALUES(changed_on), show_in_export_module = VALUES(show_in_export_module);

SELECT IF(@_item_id IS NULL, LAST_INSERT_ID(), @_item_id) AS itemId;");
                TemplateQueryStrings.Add("GET_ALL_ENTITY_TYPES", @"SET @userId = {encryptedUserId:decrypt(true)};

SELECT DISTINCT 
	IF(entity.friendly_name IS NULL OR entity.friendly_name = '', entity.name, entity.friendly_name) AS name,
    entity.name AS value
FROM wiser_entity AS entity

# Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.entity_name = entity.name

WHERE entity.show_in_search = 1
AND entity.name <> ''
AND (permission.id IS NULL OR (permission.permissions & 1) > 0)
ORDER BY IF(entity.friendly_name IS NULL OR entity.friendly_name = '', entity.name, entity.friendly_name)");
                TemplateQueryStrings.Add("GET_ITEM_ENVIRONMENTS", @"SELECT
	item.id AS id_encrypt_withdate,
	item.id AS plainItemId,
	item.published_environment,
    IFNULL(item.changed_on, item.added_on) AS changed_on
FROM wiser_item AS item
JOIN wiser_entity AS entity ON entity.name = item.entity_type AND entity.module_id = item.moduleid AND entity.enable_multiple_environments = 1
WHERE item.original_item_id = {mainItemId:decrypt(true)}
AND item.original_item_id > 0
AND item.published_environment > 0");
                TemplateQueryStrings.Add("GET_ALL_ITEMS_OF_TYPE", @"#Get all the items for the treeview
SET @mid = {moduleid};
SET @_entityType = '{entityType}';
SET @_checkId = '{checkId:decrypt(true)}';
SET @_ordering = IF('{orderBy}' LIKE '{%}', '', '{orderBy}');

SELECT 
	i.id AS id_encrypt_withdate,
  	i.title AS name,
  	0 AS haschilds,
  	we.icon AS spriteCssClass,
    we.icon AS collapsedSpriteCssClass,
    we.icon_expanded AS expandedSpriteCssClass,
  	ilp.destination_item_id AS destination_item_id_encrypt_withdate,
    IF(checked.id IS NULL, 0, 1) AS checked
FROM wiser_item i
JOIN wiser_entity we ON we.name = i.entity_type AND we.show_in_tree_view = 1
LEFT JOIN wiser_itemlink ilp ON ilp.item_id = i.id
LEFT JOIN wiser_itemlink checked ON checked.item_id = i.id AND checked.destination_item_id = @_checkId AND @_checkId <> '0'
WHERE i.moduleid = @mid
AND (@_entityType = '' OR i.entity_type = @entityType)
GROUP BY i.id
ORDER BY 
    CASE WHEN @_ordering = 'title' THEN i.title END ASC,
	CASE WHEN @_ordering <> 'title' THEN ilp.ordering END ASC");
                TemplateQueryStrings.Add("SEARCH_ITEMS", @"SET @mid = {moduleid};
SET @parent = '{id:decrypt(true)}';
SET @_entityType = IF('{entityType}' LIKE '{%}', '', '{entityType}');
SET @_searchValue = '{search}';
SET @_searchInTitle = IF('{searchInTitle}' LIKE '{%}' OR '{searchInTitle}' = '1', TRUE, FALSE);
SET @_searchFields = IF('{searchFields}' LIKE '{%}', '', '{searchFields}');
SET @_searchEverywhere = IF('{searchEverywhere}' LIKE '{%}', FALSE, {searchEverywhere});

SELECT 
	i.id,
	i.id AS encryptedId_encrypt_withdate,
	i.title AS name
FROM wiser_item i
LEFT JOIN wiser_itemlink ilp ON ilp.destination_item_id = @parent AND ilp.item_id = i.id
LEFT JOIN wiser_itemdetail id ON id.item_id = i.id
LEFT JOIN wiser_itemlink ilc ON ilc.destination_item_id = i.id
LEFT JOIN wiser_entity we ON we.name = i.entity_type

# Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id

WHERE (permission.id IS NULL OR (permission.permissions & 1) > 0)
AND i.entity_type = @_entityType
AND (@_searchEverywhere = TRUE OR ilp.id IS NOT NULL)
AND (
    (NOT @_searchInTitle AND @_searchFields = '')
    OR (@_searchInTitle AND i.title LIKE CONCAT('%', @_searchValue, '%'))
    OR (@_searchFields <> '' AND FIND_IN_SET(id.key, @_searchFields) AND id.value LIKE CONCAT('%', @_searchValue, '%'))
)

GROUP BY i.id
ORDER BY ilp.ordering, i.title");
                TemplateQueryStrings.Add("GET_DESTINATION_ITEMS", @"SET @_itemId = {itemId};
SET @_entityType = IF('{entity_type}' LIKE '{%}', 'item', '{entity_type}');
SET @_linkType = IF('{linkTypeNumber}' LIKE '{%}', '1', '{linkTypeNumber}');
SET @userId = {encryptedUserId:decrypt(true)};

SELECT 
	i.id, 
	i.id AS encryptedId_encrypt_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
	i.title, 
	i.entity_type, 
	id.`key` AS property_name,
	CONCAT(IFNULL(id.`value`, ''), IFNULL(id.`long_value`, '')) AS property_value,
	il.type AS link_type, 
    il.id AS link_id
FROM wiser_itemlink il
JOIN wiser_item i ON i.id = il.destination_item_id AND i.entity_type = @_entityType

# Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id

LEFT JOIN wiser_entityproperty p ON p.entity_name = i.entity_type
LEFT JOIN wiser_itemdetail id ON id.item_id = il.destination_item_id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND id.`key` = p.property_name) OR ((p.property_name IS NULL OR p.property_name = '') AND id.`key` = p.display_name))
WHERE il.item_id = @_itemId
AND il.type = @_linkType
AND (permission.id IS NULL OR (permission.permissions & 1) > 0)
GROUP BY il.item_id, id.id
ORDER BY il.ordering, i.title, i.id");
                TemplateQueryStrings.Add("GET_DESTINATION_ITEMS_REVERSED", @"SET @_itemId = {itemId};
SET @_entityType = IF('{entity_type}' LIKE '{%}', 'item', '{entity_type}');
SET @_linkType = IF('{linkTypeNumber}' LIKE '{%}', '1', '{linkTypeNumber}');
SET @userId = {encryptedUserId:decrypt(true)};

SELECT 
	i.id, 
	i.id AS encryptedId_encrypt_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
	i.title, 
	i.entity_type, 
	id.`key` AS property_name,
	CONCAT(IFNULL(id.`value`, ''), IFNULL(id.`long_value`, '')) AS property_value,
	il.type AS link_type,
    il.id AS link_id
FROM wiser_itemlink il
JOIN wiser_item i ON i.id = il.item_id AND i.entity_type = @_entityType

# Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id

LEFT JOIN wiser_entityproperty p ON p.entity_name = i.entity_type
LEFT JOIN wiser_itemdetail id ON id.item_id = il.item_id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND id.`key` = p.property_name) OR ((p.property_name IS NULL OR p.property_name = '') AND id.`key` = p.display_name))
WHERE il.destination_item_id = @_itemId
AND il.type = @_linkType
AND (permission.id IS NULL OR (permission.permissions & 1) > 0)
GROUP BY il.destination_item_id, id.id
ORDER BY il.ordering, i.title, i.id");
                TemplateQueryStrings.Add("ADD_LINK", @"SET @sourceId = {source:decrypt(true)};
SET @destinationId = {destination:decrypt(true)};
SET @_linkTypeNumber = IF('{linkTypeNumber}' LIKE '{%}' OR '{linkTypeNumber}' = '', '2', '{linkTypeNumber}');
SET @newOrderNumber = IFNULL((SELECT MAX(link.ordering) + 1 FROM wiser_itemlink AS link JOIN wiser_item AS item ON item.id = link.item_id WHERE link.destination_item_id = @destinationId AND link.type = @_linkTypeNumber), 1);
SET @_username = '{username}';
SET @_userId = '{encryptedUserId:decrypt(true)}';

INSERT IGNORE INTO wiser_itemlink (item_id, destination_item_id, ordering, type)
SELECT @sourceId, @destinationId, @newOrderNumber, @_linkTypeNumber
FROM DUAL
WHERE @sourceId <> @destinationId;

SELECT LAST_INSERT_ID() AS newId;");
                TemplateQueryStrings.Add("GET_COLUMNS_FOR_TABLE", @"SET @selected_id = {itemId:decrypt(true)}; # 3077

SELECT 
	CONCAT('property_.', CreateJsonSafeProperty(LOWER(IF(p.property_name IS NULL OR p.property_name = '', p.display_name, p.property_name)))) AS field,
    p.display_name AS title,
    p.overview_fieldtype AS fieldType,
    p.overview_width AS width
FROM wiser_itemlink il
JOIN wiser_item i ON i.id=il.item_id
JOIN wiser_entityproperty p ON p.entity_name=i.entity_type AND p.visible_in_overview=1
WHERE il.destination_item_id=@selected_id
GROUP BY p.property_name
ORDER BY p.ordering;");
                TemplateQueryStrings.Add("GET_DATA_FOR_TABLE", @"SET @selected_id = {itemId:decrypt(true)}; # 3077
SET @userId = {encryptedUserId:decrypt(true)};

SELECT
    i.id,
    i.id AS encryptedId_encrypt_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
    i.title,
    i.entity_type,
    CreateJsonSafeProperty(LOWER(IF(id.id IS NOT NULL, id.`key`, id2.`key`))) AS property_name,
    IF(id.id IS NOT NULL, id.`value`, id2.`value`) AS property_value,
    il.id AS link_id
FROM wiser_itemlink il
JOIN wiser_item i ON i.id = il.item_id

LEFT JOIN wiser_entityproperty p ON p.entity_name = i.entity_type AND p.visible_in_overview = 1
LEFT JOIN wiser_itemdetail id ON id.item_id = il.item_id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND id.`key` = p.property_name) OR ((p.property_name IS NULL OR p.property_name = '') AND id.`key` = p.display_name))

LEFT JOIN wiser_entityproperty p2 ON p2.link_type = il.type AND p.visible_in_overview = 1
LEFT JOIN wiser_itemlinkdetail id2 ON id2.itemlink_id = il.id AND ((p2.property_name IS NOT NULL AND p2.property_name <> '' AND id2.`key` = p2.property_name) OR ((p2.property_name IS NULL OR p2.property_name = '') AND id2.`key` = p2.display_name))

# Check permissions. Default permissions are everything enabled, so if the user has no role or the role has no permissions on this item, they are allowed everything.
LEFT JOIN wiser_user_roles user_role ON user_role.user_id = @userId
LEFT JOIN wiser_permission permission ON permission.role_id = user_role.role_id AND permission.item_id = i.id

WHERE il.destination_item_id = @selected_id
AND (permission.id IS NULL OR (permission.permissions & 1) > 0)
GROUP BY il.item_id, IFNULL(id.id, id2.id)
ORDER BY il.ordering, i.title");
                TemplateQueryStrings.Add("GET_ITEMLINKS_BY_ENTITY", @"SELECT
    # reuse fields to define text and values for the kendo dropdown
    type AS type_text,
    type AS type_value
FROM wiser_itemlink AS link
JOIN wiser_item AS item ON item.id = link.item_id AND item.entity_type = '{entity_name}'
GROUP BY type");
                TemplateQueryStrings.Add("GET_ENTITY_PROPERTIES", @"SET @_entity_name = IF(
    '{entity_name}' NOT LIKE '{%}',
    '{entity_name}',
    # Check for old query string name.
    IF(
        '{entity_types}' NOT LIKE '{%}',
        SUBSTRING_INDEX('{entity_types}', ',', 1),
        ''
    )
);

SELECT
    property.`value`,
    property.entityName,
    # The display name is used in the field editor.
    property.displayName,
    # These fields are just to ensure the properties exist in the Kendo data item.
    '' AS languageCode,
    '' AS aggregation,
    '' AS formatting,
    '' AS fieldAlias,
    'ASC' AS direction,
    # These fields are deprecated and will be removed in the future.
    property.text,
    property.text AS originalText
FROM (
    # ID.
    SELECT 'ID' AS displayName, 'ID' AS text, 'id' AS `value`, @_entity_name AS entityName, 0 AS dynamicField, '0' AS sort

    UNION

    # Encrypted ID.
    SELECT 'Versleuteld ID' AS displayName, 'Versleuteld ID' AS text, 'idencrypted' AS `value`, @_entity_name AS entityName, 0 AS dynamicField, '1' AS sort

    UNION

    # Unique ID.
    SELECT 'Uniek ID' AS displayName, 'Uniek ID' AS text, 'unique_uuid' AS `value`, @_entity_name AS entityName, 0 AS dynamicField, '2' AS sort

    UNION

    # Title.
    SELECT 'Item titel' AS displayName, 'Item titel' AS text, 'itemtitle' AS `value`, @_entity_name AS entityName, 0 AS dynamicField, '3' AS sort

    UNION

    # Changed on.
    SELECT 'Gewijzigd op' AS displayName, 'Gewijzigd op' AS text, 'changed_on' AS `value`, @_entity_name AS entityName, 0 AS dynamicField, '4' AS sort

    UNION

    # Changed by.
    SELECT 'Gewijzigd door' AS displayName, 'Gewijzigd door' AS text, 'changed_by' AS `value`, @_entity_name AS entityName, 0 AS dynamicField, '5' AS sort

    UNION

    # Entity properties.
    (SELECT
        IF(
            # Check if there are more than one properties with the same property name.
            COUNT(*) > 1,
            # If True; Use the property name with the character capitalizted to create the display name.
            CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)),
            # If False; Use the property's own display name.
            display_name
        ) AS displayName,
        CONCAT_WS(' - ', entity_name, IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name)) AS text,
        IF(property_name = '', CreateJsonSafeProperty(display_name), property_name) AS `value`,
        entity_name AS entityName,
        1 AS dynamicField,
        IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name) AS sort
    FROM wiser_entityproperty
    WHERE
     	entity_name = @_entity_name
        # Some entities should be ignored due to their input types.
        AND inputtype NOT IN (
            'action-button',
            'auto-increment',
            'button',
            'chart',
            'data-selector',
            'empty',
            'file-upload',
            'grid',
            'image-upload',
            'item-linker',
            'linked-item',
            'querybuilder',
            'scheduler',
            'sub-entities-grid',
            'timeline'
        )
    GROUP BY `value`)

    UNION

    # SEO variants of the entity properties.
    (SELECT
        CONCAT(
            IF(
                # Check if there are more than one properties with the same property name.
                COUNT(*) > 1,
                # If True; Use the property name with the character capitalizted to create the display name.
                CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)),
                # If False; Use the property's own display name.
                display_name
            ),
            ' (SEO)'
        ) AS displayName,
        CONCAT(CONCAT_WS(' - ', entity_name, IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name)), ' (SEO)') AS text,
        CONCAT(IF(property_name = '', CreateJsonSafeProperty(display_name), property_name), '_SEO') AS `value`,
        entity_name AS entityName,
        1 AS dynamicField,
        CONCAT(IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name), ' (SEO)') AS sort
    FROM wiser_entityproperty
    WHERE
     	entity_name = @_entity_name
        # Some entities should be ignored due to their input types.
        AND inputtype NOT IN (
            'action-button',
            'auto-increment',
            'button',
            'chart',
            'data-selector',
            'empty',
            'file-upload',
            'grid',
            'image-upload',
            'item-linker',
            'linked-item',
            'querybuilder',
            'scheduler',
            'sub-entities-grid',
            'timeline'
        )
        AND also_save_seo_value = 1
    GROUP BY `value`)
) AS property
# Static fields first, then order by the 'sort' value.
ORDER BY property.dynamicField, property.sort");
                TemplateQueryStrings.Add("GET_ENTITY_LINK_PROPERTIES", @"SET @_link_type = IF(
    '{link_type}' NOT LIKE '{%}',
    CONVERT('{link_type}', SIGNED),
    -1
);

SELECT
    property.`value`,
    property.linkType,
    # The display name is used in the field editor.
    property.displayName,
    # These fields are just to ensure the properties exist in the Kendo data item.
    '' AS languageCode,
    '' AS aggregation,
    '' AS formatting,
    '' AS fieldAlias,
    'ASC' AS direction,
    # These fields are deprecated and will be removed in the future.
    property.text,
    property.text AS originalText
FROM (
    # Entity link properties.
    (SELECT
        IF(
            # Check if there are more than one properties with the same property name.
            COUNT(*) > 1,
            # If True; Use the property name with the character capitalizted to create the display name.
            CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)),
            # If False; Use the property's own display name.
            display_name
        ) AS displayName,
        CONCAT_WS(' - ', entity_name, IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name)) AS text,
        IF(property_name = '', CreateJsonSafeProperty(display_name), property_name) AS `value`,
        link_type AS linkType,
        1 AS dynamicField,
        IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name) AS sort
    FROM wiser_entityproperty
    WHERE
     	link_type = @_link_type
        # Some entities should be ignored due to their input types.
        AND inputtype NOT IN (
            'action-button',
            'auto-increment',
            'button',
            'chart',
            'data-selector',
            'empty',
            'file-upload',
            'grid',
            'image-upload',
            'item-linker',
            'linked-item',
            'querybuilder',
            'scheduler',
            'sub-entities-grid',
            'timeline'
        )
    GROUP BY `value`)

    UNION

    # SEO variants of the entity properties.
    (SELECT
        CONCAT(
            IF(
                # Check if there are more than one properties with the same property name.
                COUNT(*) > 1,
                # If True; Use the property name with the character capitalizted to create the display name.
                CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)),
                # If False; Use the property's own display name.
                display_name
            ),
            ' (SEO)'
        ) AS displayName,
        CONCAT(CONCAT_WS(' - ', entity_name, IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name)), ' (SEO)') AS text,
        CONCAT(IF(property_name = '', CreateJsonSafeProperty(display_name), property_name), '_SEO') AS `value`,
        link_type AS linkType,
        1 AS dynamicField,
        CONCAT(IF(COUNT(*) > 1, CONCAT(UPPER(SUBSTR(property_name, 1, 1)), SUBSTR(property_name, 2)), display_name), ' (SEO)') AS sort
    FROM wiser_entityproperty
    WHERE
     	link_type = @_link_type
        # Some entities should be ignored due to their input types.
        AND inputtype NOT IN (
            'action-button',
            'auto-increment',
            'button',
            'chart',
            'data-selector',
            'empty',
            'file-upload',
            'grid',
            'image-upload',
            'item-linker',
            'linked-item',
            'querybuilder',
            'scheduler',
            'sub-entities-grid',
            'timeline'
        )
        AND also_save_seo_value = 1
    GROUP BY `value`)
) AS property
# Static fields first, then order by the 'sort' value.
ORDER BY property.dynamicField, property.sort");
                TemplateQueryStrings.Add("GET_LINKED_TO_ITEMS", @"#Get all the items for the treeview
SET @_module_id = IF('{module}' LIKE '{%}', '', '{module}');
SET @_parent_id = IF('{id:decrypt(true)}' LIKE '{%}', '0', '{id:decrypt(true)}');

SELECT 
	i.id AS id_encrypt_withdate,
  	i.title AS name,
  	IF(i2.id IS NULL OR i2.moduleid <> @_module_id, 0, 1) AS haschilds,
  	we.icon AS spriteCssClass,
    we.icon AS collapsedSpriteCssClass,
    we.icon_expanded AS expandedSpriteCssClass,
  	ilp.destination_item_id AS destination_item_id_encrypt_withdate,
    0 AS checked
FROM wiser_item i
JOIN wiser_entity we ON we.name = i.entity_type AND we.show_in_tree_view = 1
JOIN wiser_itemlink ilp ON ilp.destination_item_id = @_parent_id AND ilp.item_id = i.id
LEFT JOIN wiser_itemlink ilc ON ilc.destination_item_id = i.id
LEFT JOIN wiser_item i2 ON i2.id = ilc.item_id
WHERE i.moduleid = @_module_id
GROUP BY i.id
ORDER BY ilp.ordering");
                TemplateQueryStrings.Add("GET_DATA_FOR_FIELD_TABLE", @"SET @selected_id = {itemId:decrypt(true)};
SET @entitytype = IF('{entity_type}' LIKE '{%}', '', '{entity_type}');
SET @_moduleId = IF('{moduleId}' LIKE '{%}', '', '{moduleId}');
SET @_linkTypeNumber = IF('{linkTypeNumber}' LIKE '{%}', '', '{linkTypeNumber}');

SELECT
	i.id,
	i.id AS encryptedId_encrypt_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
    i.title,
    i.entity_type,
	CreateJsonSafeProperty(LOWER(id.key)) AS property_name,
	IFNULL(idt.`value`, id.`value`) AS property_value,
    il.type AS link_type_number,
    il.id AS link_id,
	il.ordering
FROM wiser_itemlink il
JOIN wiser_item i ON i.id = il.item_id AND (@entitytype = '' OR FIND_IN_SET(i.entity_type, @entitytype)) AND (@_moduleId = '' OR @_moduleId = i.moduleid)

LEFT JOIN wiser_entityproperty p ON p.entity_name = i.entity_type AND p.visible_in_overview = 1
LEFT JOIN wiser_itemdetail id ON id.item_id = il.item_id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND id.`key` = p.property_name) OR ((p.property_name IS NULL OR p.property_name = '') AND id.`key` = p.display_name))
LEFT JOIN wiser_itemdetail idt ON idt.item_id = il.item_id AND ((p.property_name IS NOT NULL AND p.property_name <> '' AND idt.`key` = CONCAT(p.property_name, '_input')) OR ((p.property_name IS NULL OR p.property_name = '') AND idt.`key` = CONCAT(p.display_name, '_input')))

WHERE il.destination_item_id = @selected_id
AND (@_linkTypeNumber = '' OR il.type = @_linkTypeNumber)
GROUP BY il.item_id, id.id

UNION

SELECT
	i.id,
	i.id AS encryptedId_encrypt_withdate,
    CASE i.published_environment
    	WHEN 0 THEN 'onzichtbaar'
        WHEN 1 THEN 'dev'
        WHEN 2 THEN 'test'
        WHEN 3 THEN 'acceptatie'
        WHEN 4 THEN 'live'
    END AS published_environment,
    i.title,
    i.entity_type,
	CreateJsonSafeProperty(id2.key) AS property_name,
	IFNULL(id2t.`value`, id2.`value`) AS property_value,
    il.type AS link_type_number,
    il.id AS link_id,
	il.ordering
FROM wiser_itemlink il
JOIN wiser_item i ON i.id = il.item_id AND (@entitytype = '' OR FIND_IN_SET(i.entity_type, @entitytype)) AND (@_moduleId = '' OR @_moduleId = i.moduleid)

LEFT JOIN wiser_entityproperty p2 ON p2.link_type = il.type AND p2.visible_in_overview = 1
LEFT JOIN wiser_itemlinkdetail id2 ON id2.itemlink_id = il.id AND ((p2.property_name IS NOT NULL AND p2.property_name <> '' AND id2.`key` = p2.property_name) OR ((p2.property_name IS NULL OR p2.property_name = '') AND id2.`key` = p2.display_name))
LEFT JOIN wiser_itemlinkdetail id2t ON id2t.itemlink_id = il.id AND ((p2.property_name IS NOT NULL AND p2.property_name <> '' AND id2t.`key` = CONCAT(p2.property_name, '_input')) OR ((p2.property_name IS NULL OR p2.property_name = '') AND id2t.`key` = CONCAT(p2.display_name, '_input')))

WHERE il.destination_item_id = @selected_id
AND (@_linkTypeNumber = '' OR il.type = @_linkTypeNumber)
GROUP BY il.item_id, id2.id

ORDER BY ordering, title");
                TemplateQueryStrings.Add("GET_ITEM_FILES_AND_DIRECTORIES", @"SET @parent = IF('{id}' = '' OR '{id}' LIKE '{%}', '{rootId:decrypt(true)}', '{id:decrypt(true)}');

SELECT
	id AS id_encrypt_withdate,
    id AS plainId,
	file_name AS name,
	content_type AS contentType,
	0 AS isDirectory,
	0 AS childrenCount,
    property_name,
    item_id AS itemId_encrypt_withdate,
    item_id AS itemIdPlain,
    CASE
        WHEN content_type LIKE 'image/%' THEN 'image'
        WHEN content_type = 'text/html' THEN 'html'
        ELSE 'file'
    END AS spriteCssClass,
    IF(content_type IN('text/html', 'application/octet-stream'), CONVERT(content USING utf8), '') AS html
FROM wiser_itemfile
WHERE item_id = @parent

UNION ALL

SELECT
	item.id AS id_encrypt_withdate,
    item.id AS plainId,
	item.title AS name,
	'' AS contentType,
	1 AS isDirectory,
	COUNT(DISTINCT subItem.id) + COUNT(DISTINCT file.id) AS childrenCount,
    '' AS property_name,
	item.id AS itemId_encrypt_withdate,
    item.id AS itemIdPlain,
    'wiserfolderclosed' AS spriteCssClass,
    '' AS html
FROM wiser_item AS item
LEFT JOIN wiser_item AS subItem ON subItem.entity_type = 'filedirectory' AND subItem.parent_item_id = item.id
LEFT JOIN wiser_itemfile AS file ON file.item_id = item.id
WHERE item.entity_type = 'filedirectory'
AND item.parent_item_id = @parent
GROUP BY item.id

ORDER BY isDirectory DESC, name ASC");

                TemplateQueryStrings.Add("GET_DATA_FROM_ENTITY_QUERY", @"SET @_itemId = {myItemId};
SET @entityproperty_id = {propertyid};
SET @querytext = (SELECT REPLACE(REPLACE(IFNULL(data_query, 'SELECT 0 AS id, "" AS name'), '{itemId}', @_itemId), '{itemid}', @_itemId) FROM wiser_entityproperty WHERE id=@entityproperty_id);

PREPARE stmt1 FROM @querytext;
EXECUTE stmt1;");
            }
        }

        /// <inheritdoc />
        public async Task<ServiceResult<TemplateModel>> GetTemplateByName(string templateName, bool wiserTemplate = false)
        {
            var connectionToUse = clientDatabaseConnection;

            if (wiserTemplate)
            {
                connectionToUse = wiserDatabaseConnection;
            }

            if (connectionToUse == null)
            {
                return new ServiceResult<TemplateModel>(new TemplateModel());
            }

            await connectionToUse.EnsureOpenConnectionForReadingAsync();
            connectionToUse.ClearParameters();
            connectionToUse.AddParameter("templateName", templateName);

            var query = @"
SELECT
    item.id AS id,
    item.title AS `name`,
    `subject`.`value` AS `subject`,
    IF(template.long_value IS NULL OR template.long_value = '', template.`value`, template.long_value) AS content
FROM wiser_item AS item
JOIN wiser_itemdetail AS template ON template.item_id = item.id AND template.`key` = 'template'
JOIN wiser_itemdetail AS `subject` ON `subject`.item_id = item.id AND `subject`.`key` = 'subject'
WHERE item.entity_type = 'template'
AND item.title = ?templateName
LIMIT 1";

            var dataTable = await connectionToUse.GetAsync(query);

            if (dataTable.Rows.Count == 0)
            {
                return new ServiceResult<TemplateModel>(new TemplateModel());
            }
            
            var result = new TemplateModel()
            {
                Id = dataTable.Rows[0].Field<ulong>("id"),
                Name = dataTable.Rows[0].Field<string>("name"),
                Subject = dataTable.Rows[0].Field<string>("subject"),
                Content = dataTable.Rows[0].Field<string>("content")
            };

            return new ServiceResult<TemplateModel>(result);
        }
    }
}