﻿import { Wiser2 } from "../../Base/Scripts/Utils.js";

require("@progress/kendo-ui/js/kendo.tooltip.js");
require("@progress/kendo-ui/js/kendo.button.js");
require("@progress/kendo-ui/js/kendo.dialog.js");
require("@progress/kendo-ui/js/kendo.grid.js");
require("@progress/kendo-ui/js/cultures/kendo.culture.nl-NL.js");
require("@progress/kendo-ui/js/messages/kendo.messages.nl-NL.js");

/**
 * Class for any and all functionality for grids.
 */
export class Grids {

    /**
     * Initializes a new instance of the Grids class.
     * @param {DynamicItems} base An instance of the base class (DynamicItems).
     */
    constructor(base) {
        this.base = base;

        this.mainGrid = null;
        this.mainGridFirstLoad = true;
        this.mainGridForceRecount = false;
    }

    /**
     * Do all initializations for the Grids class, such as adding bindings.
     */
    async initialize() {
        if (this.base.settings.gridViewMode && !this.base.settings.iframeMode) {
            this.base.settings.gridViewSettings = this.base.settings.gridViewSettings || {};

            const hideGrid = await this.setupInformationBlock();
            if (!hideGrid) {
                this.setupGridViewMode();
            }
        }
    }

    /**
     * Setup the main information block for when the module has gridViewMode enabled and the informationBlock enabled.
     * @returns {boolean} Whether or not the grid view should be hidden.
     */
    async setupInformationBlock() {
        let hideGrid = false;
        const informationBlockSettings = this.base.settings.gridViewSettings.informationBlock;

        if (!informationBlockSettings || !informationBlockSettings.initialItem) {
            return hideGrid;
        }

        this.base.settings.openGridItemsInBlock = informationBlockSettings.openGridItemsInBlock;
        this.base.settings.showSaveAndCreateNewItemButton = informationBlockSettings.showSaveAndCreateNewItemButton;
        this.base.settings.hideDefaultSaveButton = informationBlockSettings.hideDefaultSaveButton;

        const initialProcess = `loadInformationBlock_${Date.now()}`;

        try {
            jjl.processing.addProcess(initialProcess);
            const mainContainer = $("#wiser").addClass(`with-information-block information-${informationBlockSettings.position || "bottom"}`);
            const informationBlockContainer = $("#informationBlock").removeClass("hidden").addClass(informationBlockSettings.position || "bottom");
            if (informationBlockSettings.height) {
                informationBlockContainer.css("flex-basis", informationBlockSettings.height);
            }

            if (informationBlockSettings.width) {
                informationBlockContainer.css("width", informationBlockSettings.width);
                if (informationBlockSettings.width.indexOf("%") > -1) {
                    const informationBlockWidth = parseInt(informationBlockSettings.width.replace("%", ""));
                    $("#gridView").css("width", `${(100 - informationBlockWidth)}%`);
                    hideGrid = informationBlockWidth >= 100;
                    if (informationBlockWidth >= 100) {
                        informationBlockContainer.addClass("full");
                    }
                }
            }

            this.informationBlockIframe = $(`<iframe />`).appendTo(informationBlockContainer);
            this.informationBlockIframe[0].onload = () => {
                jjl.processing.removeProcess(initialProcess);

                dynamicItems.grids.informationBlockIframe[0].contentDocument.addEventListener("dynamicItems.onSaveButtonClick", () => {
                    if (!this.mainGrid || !this.mainGrid.dataSource) {
                        return;
                    }

                    this.mainGrid.dataSource.read();
                });

                if (this.base.settings.hideDefaultSaveButton) {
                    this.informationBlockIframe[0].contentWindow.$("#saveBottom").addClass("hidden").data("hidden-via-parent", true);
                }

                if (!this.base.settings.showSaveAndCreateNewItemButton) {
                    return;
                }

                this.informationBlockIframe[0].contentWindow.$("#saveAndCreateNewItemButton").removeClass("hidden").data("shown-via-parent", true).kendoButton({
                    click: async (event) => {
                        if (!(await this.informationBlockIframe[0].contentWindow.dynamicItems.onSaveButtonClick(event))) {
                            return false;
                        }

                        const createItemResult = await this.base.createItem(informationBlockSettings.initialItem.entityType, informationBlockSettings.initialItem.newItemParentId, "", null, [], true);
                        if (!createItemResult) {
                            return hideGrid;
                        }
                        const itemId = createItemResult.itemId;
                        this.informationBlockIframe.attr("src", `${"/Modules/DynamicItems"}?itemId=${itemId}&moduleId=${this.base.settings.moduleId}&iframe=true&readonly=${!!informationBlockSettings.initialItem.readOnly}&hideFooter=${!!informationBlockSettings.initialItem.hideFooter}&hideHeader=${!!informationBlockSettings.initialItem.hideHeader}`);
                    },
                    icon: "save"
                });
            };

            let itemId = informationBlockSettings.initialItem.itemId;
            if (!itemId) {
                const createItemResult = await this.base.createItem(informationBlockSettings.initialItem.entityType, informationBlockSettings.initialItem.newItemParentId, "", null, [], true);
                if (!createItemResult) {
                    return hideGrid;
                }
                itemId = createItemResult.itemId;
            }

            this.informationBlockIframe.attr("src", `${"/Modules/DynamicItems"}?itemId=${itemId}&moduleId=${this.base.settings.moduleId}&iframe=true&readonly=${!!informationBlockSettings.initialItem.readOnly}&hideFooter=${!!informationBlockSettings.initialItem.hideFooter}&hideHeader=${!!informationBlockSettings.initialItem.hideHeader}`);
        } catch (exception) {
            kendo.alert("Er is iets fout gegaan tijdens het laden van de data voor deze module. Sluit a.u.b. de module en probeer het nogmaals, of neem contact op met ons.");
            console.error(exception);
            jjl.processing.removeProcess(initialProcess);
        }

        return hideGrid;
    }

    /**
     * Setup the main grid for when the module has gridViewMode enabled.
     */
    async setupGridViewMode() {
        const initialProcess = `loadMainGrid_${Date.now()}`;

        try {
            jjl.processing.addProcess(initialProcess);
            let gridViewSettings = $.extend({}, this.base.settings.gridViewSettings);
            let gridDataResult;
            let previousFilters = null;

            const usingDataSelector = !!gridViewSettings.dataSelectorId;
            if (usingDataSelector) {
                gridDataResult = {
                    columns: gridViewSettings.columns,
                    page_size: gridViewSettings.pageSize || 100,
                    data: await Wiser2.api({
                        url: `${this.base.settings.getItemsUrl}?encryptedDataSelectorId=${encodeURIComponent(gridViewSettings.dataSelectorId)}&userEmailAddress=${encodeURIComponent(this.base.settings.userEmailAddress)}`,
                        contentType: "application/json"
                    })
                };

                if (gridDataResult.data && gridDataResult.data.length > 0 && (!gridDataResult.columns || !gridDataResult.columns.length)) {
                    gridDataResult.columns = [];
                    let data = gridDataResult.data[0];
                    for (let key in data) {
                        if (!data.hasOwnProperty(key)) {
                            continue;
                        }

                        gridDataResult.columns.push({ title: key, field: key });
                    }
                }
            } else {
                const options = {
                    page: 1,
                    page_size: gridViewSettings.pageSize || 100,
                    skip: 0,
                    take: gridViewSettings.clientSidePaging ? 0 : (gridViewSettings.pageSize || 100),
                    first_load: true
                };

                if (gridViewSettings.dataSource && gridViewSettings.dataSource.filter) {
                    options.filter = gridViewSettings.dataSource.filter;
                    previousFilters = JSON.stringify(options.filter);
                }

                gridDataResult = await Wiser2.api({
                    url: `${this.base.settings.wiserApiRoot}modules/${encodeURIComponent(this.base.settings.moduleId)}/overview-grid`,
                    method: "POST",
                    contentType: "application/json",
                    data: JSON.stringify(options)
                });

                if (gridDataResult.extra_javascript) {
                    $.globalEval(gridDataResult.extra_javascript);
                }
            }

            let disableOpeningOfItems = gridViewSettings.disableOpeningOfItems;
            if (!disableOpeningOfItems) {
                if (gridDataResult.schema_model && gridDataResult.schema_model.fields) {
                    // If there is no field for encrypted ID, don't allow the user to open items, they'd just get an error.
                    disableOpeningOfItems = !(gridDataResult.schema_model.fields.encryptedId || gridDataResult.schema_model.fields.encrypted_id || gridDataResult.schema_model.fields.encryptedid || gridDataResult.schema_model.fields.idencrypted);
                }
            }

            if (!gridViewSettings.hideCommandColumn) {
                let commandColumnWidth = 80;
                const commands = [];


                if (!disableOpeningOfItems) {
                    commands.push({
                        name: "openDetails",
                        iconClass: "k-icon k-i-hyperlink-open",
                        text: "",
                        click: (event) => { this.base.grids.onShowDetailsClick(event, this.mainGrid, { customQuery: true, usingDataSelector: usingDataSelector, fromMainGrid: true }); }
                    });
                }

                if (gridViewSettings.deleteItemQueryId && (typeof (gridViewSettings.showDeleteButton) === "undefined" || gridViewSettings.showDeleteButton === true)) {
                    commandColumnWidth += 40;

                    const onDeleteClick = async (event) => {
                        if (!gridViewSettings || gridViewSettings.showDeleteConformations !== false) {
                            await kendo.confirm("Weet u zeker dat u dit item wilt verwijderen?");
                        }

                        const mainItemDetails = this.mainGrid.dataItem($(event.currentTarget).closest("tr"));
                        await Wiser2.api({
                            method: "POST",
                            url: `${this.base.settings.wiserApiRoot}items/${encodeURIComponent(mainItemDetails.encryptedId || mainItemDetails.encrypted_id || mainItemDetails.encryptedid || this.base.settings.zeroEncrypted)}/action-button/0?queryId=${encodeURIComponent(gridViewSettings.deleteItemQueryId)}&userEmailAddress=${encodeURIComponent(this.base.settings.userEmailAddress)}&itemLinkId=${encodeURIComponent(mainItemDetails.link_id || mainItemDetails.linkId || 0)}`,
                            data: JSON.stringify(mainItemDetails),
                            contentType: "application/json"
                        });

                        this.mainGrid.dataSource.read();
                    };

                    commands.push({
                        name: "remove",
                        iconClass: "k-icon k-i-delete",
                        text: "",
                        click: onDeleteClick.bind(this)
                    });
                }
                else if (gridViewSettings.showDeleteButton === true) {
                    commandColumnWidth += 40;

                    commands.push({
                        name: "remove",
                        text: "",
                        iconClass: "k-icon k-i-delete",
                        click: (event) => { this.base.grids.onDeleteItemClick(event, this.mainGrid, "deleteItem", gridViewSettings); }
                    });
                }

                if (gridDataResult.columns) {
                    gridDataResult.columns.push({
                        title: "&nbsp;",
                        width: commandColumnWidth,
                        command: commands
                    });
                }
            }

            const toolbar = [];

            if (!gridViewSettings.toolbar || !gridViewSettings.toolbar.hideRefreshButton) {
                toolbar.push({
                    name: "refreshCustom",
                    iconClass: "k-icon k-i-refresh",
                    text: "",
                    template: `<a class='k-button k-button-icontext k-grid-refresh' href='\\#' title='Verversen'><span class='k-icon k-i-refresh'></span></a>`
                });
            }

            if (!gridViewSettings.toolbar || !gridViewSettings.toolbar.hideClearFiltersButton) {
                toolbar.push({
                    name: "clearAllFilters",
                    text: "",
                    template: `<a class='k-button k-button-icontext clear-all-filters' title='Alle filters wissen' href='\\#' onclick='return window.dynamicItems.grids.onClearAllFiltersClick(event)'><span class='k-icon k-i-filter-clear'></span></a>`
                });
            }

            if (!gridViewSettings.toolbar || !gridViewSettings.toolbar.hideCount) {
                toolbar.push({
                    name: "count",
                    iconClass: "",
                    text: "",
                    template: `<div class="counterContainer"><span class="counter">0</span> <span class="plural">resultaten</span><span class="singular" style="display: none;">resultaat</span></div>`
                });
            }

            if (!gridViewSettings.toolbar || !gridViewSettings.toolbar.hideExportButton) {
                toolbar.push({
                    name: "excel"
                });
            }


            if ((!gridViewSettings.toolbar || !gridViewSettings.toolbar.hideCreateButton) && this.base.settings.permissions.can_create) {
                toolbar.push({
                    name: "add",
                    text: "Nieuw",
                    template: `<a class='k-button k-button-icontext' href='\\#' onclick='return window.dynamicItems.dialogs.openCreateItemDialog(null, null, null, ${gridViewSettings.skipNameForNewItems})'><span class='k-icon k-i-file-add'></span>Nieuw item toevoegen</a>`
                });
            }

            if (gridViewSettings.toolbar && gridViewSettings.toolbar.customActions && gridViewSettings.toolbar.customActions.length > 0) {
                for (let i = 0; i < gridViewSettings.toolbar.customActions.length; i++) {
                    const customAction = gridViewSettings.toolbar.customActions[i];

                    // Check permissions.
                    if (customAction.doesCreate && !this.base.settings.permissions.can_create) {
                        continue;
                    }
                    if (customAction.doesUpdate && !this.base.settings.permissions.can_write) {
                        continue;
                    }
                    if (customAction.doesDelete && !this.base.settings.permissions.can_delte) {
                        continue;
                    }

                    toolbar.push({
                        name: `customAction${i.toString()}`,
                        text: customAction.text,
                        template: `<a class='k-button k-button-icontext' href='\\#' onclick='return window.dynamicItems.fields.onSubEntitiesGridToolbarActionClick("\\#gridView", 0, 0, ${JSON.stringify(customAction)}, event)' style='${(kendo.htmlEncode(customAction.style || ""))}'><span class='k-icon k-i-${customAction.icon}'></span>${customAction.text}</a>`
                    });
                }
            }

            let totalResults = gridDataResult.total_results;

            // Setup filters. They are turned off by default, but can be turned on with default settings.
            let filterable = false;
            const defaultFilters = {
                extra: false,
                operators: {
                    string: {
                        startswith: "Begint met",
                        eq: "Is gelijk aan",
                        neq: "Is ongelijk aan",
                        contains: "Bevat",
                        doesnotcontain: "Bevat niet",
                        endswith: "Eindigt op",
                        "isnull": "Is leeg",
                        "isnotnull": "Is niet leeg"
                    }
                },
                messages: {
                    isTrue: "<span>Ja</span>",
                    isFalse: "<span>Nee</span>"
                }
            };

            if (gridViewSettings.filterable === true) {
                filterable = defaultFilters;
            } else if (typeof gridViewSettings.filterable === "object") {
                filterable = $.extend(true, {}, defaultFilters, gridViewSettings.filterable);
            }

            // Delete properties that we have already defined, so that they won't be overwritten again by the $.extend below.
            delete gridViewSettings.filterable;
            delete gridViewSettings.toolbar;

            let columns = gridViewSettings.columns || [];
            if (columns) {
                if (gridDataResult.columns && gridDataResult.columns.length > 0) {
                    for (let column of gridDataResult.columns) {
                        const filtered = columns.filter(c => (c.field || "").toLowerCase() === (column.field || "").toLowerCase());
                        if (filtered.length > 0) {
                            continue;
                        }

                        columns.push(column);
                    }
                }

                if (columns.length === 0) {
                    columns = undefined; // So that Kendo auto generated the columns, it won't do that if we give an empty array.
                } else {
                    columns = columns.map(e => {
                        const result = e;
                        if (result.field) {
                            result.field = result.field.toLowerCase();
                        }
                        return result;
                    });
                }
            }

            const finalGridViewSettings = $.extend(true, {
                dataSource: {
                    serverPaging: !usingDataSelector && !gridViewSettings.clientSidePaging,
                    serverSorting: !usingDataSelector && !gridViewSettings.clientSideSorting,
                    serverFiltering: !usingDataSelector && !gridViewSettings.clientSideFiltering,
                    pageSize: gridDataResult.page_size,
                    transport: {
                        read: async (transportOptions) => {
                            const process = `loadMainGrid_${Date.now()}`;

                            try {
                                if (this.mainGridFirstLoad) {
                                    transportOptions.success(gridDataResult);
                                    this.mainGridFirstLoad = false;
                                    jjl.processing.removeProcess(initialProcess);
                                    return;
                                }

                                if (!transportOptions.data) {
                                    transportOptions.data = {};
                                }

                                jjl.processing.addProcess(process);

                                // If we're using the same filters as before, we don't need to count the total amount of results again, 
                                // so we tell the API whether this is the case, so that it can skip the execution of the count query, to make scrolling through the grid faster.
                                let currentFilters = null;
                                if (transportOptions.data.filter) {
                                    currentFilters = JSON.stringify(transportOptions.data.filter);
                                }

                                transportOptions.data.first_load = this.mainGridForceRecount || currentFilters !== previousFilters;
                                transportOptions.data.page_size = transportOptions.data.pageSize;
                                previousFilters = currentFilters;
                                this.mainGridForceRecount = false;

                                let newGridDataResult;
                                if (usingDataSelector) {
                                    newGridDataResult = {
                                        columns: gridViewSettings.columns,
                                        page_size: gridViewSettings.pageSize || 100,
                                        data: await Wiser2.api({
                                            url: `${this.base.settings.getItemsUrl}?encryptedDataSelectorId=${encodeURIComponent(gridViewSettings.dataSelectorId)}&userEmailAddress=${encodeURIComponent(this.base.settings.userEmailAddress)}`,
                                            contentType: "application/json"
                                        })
                                    };
                                } else {
                                    newGridDataResult = await Wiser2.api({
                                        url: `${this.base.settings.wiserApiRoot}modules/${encodeURIComponent(this.base.settings.moduleId)}/overview-grid`,
                                        method: "POST",
                                        contentType: "application/json",
                                        data: JSON.stringify(transportOptions.data)
                                    });
                                }

                                if (typeof newGridDataResult.total_results !== "number" || !transportOptions.data.first_load) {
                                    newGridDataResult.total_results = totalResults;
                                } else if (transportOptions.data.first_load) {
                                    totalResults = newGridDataResult.total_results;
                                }

                                transportOptions.success(newGridDataResult);
                            } catch (exception) {
                                console.error(exception);
                                transportOptions.error(exception);
                                kendo.alert("Er is iets fout gegaan tijdens het laden van de data voor deze module. Sluit a.u.b. de module en probeer het nogmaals, of neem contact op met ons.");
                            }

                            jjl.processing.removeProcess(process);
                        }
                    },
                    schema: {
                        data: "data",
                        total: "total_results",
                        model: gridDataResult.schema_model
                    }
                },
                excel: {
                    fileName: "Module Export.xlsx",
                    filterable: true,
                    allPages: true
                },
                columnHide: this.saveGridViewState.bind(this, `main_grid_columns_${this.base.settings.moduleId}`),
                columnShow: this.saveGridViewState.bind(this, `main_grid_columns_${this.base.settings.moduleId}`),
                dataBound: (event) => {
                    const totalCount = event.sender.dataSource.total();
                    const counterContainer = event.sender.element.find(".k-grid-toolbar .counterContainer");
                    counterContainer.find(".counter").html(kendo.toString(totalCount, "n0"));
                    counterContainer.find(".plural").toggle(totalCount !== 1);
                    counterContainer.find(".singular").toggle(totalCount === 1);
                },
                resizable: true,
                sortable: true,
                scrollable: usingDataSelector ? true : {
                    virtual: true
                },
                filterable: filterable,
                filterMenuInit: this.onFilterMenuInit.bind(this),
                filterMenuOpen: this.onFilterMenuOpen.bind(this)
            }, gridViewSettings);

            finalGridViewSettings.selectable = gridViewSettings.selectable || false;
            finalGridViewSettings.toolbar = toolbar.length === 0 ? null : toolbar;
            finalGridViewSettings.columns = columns;

            this.mainGrid = $("#gridView").kendoGrid(finalGridViewSettings).data("kendoGrid");

            await this.loadGridViewState(`main_grid_columns_${this.base.settings.moduleId}`, this.mainGrid);

            if (!disableOpeningOfItems) {
                this.mainGrid.element.on("dblclick", "tbody tr[data-uid] td", (event) => { this.base.grids.onShowDetailsClick(event, this.mainGrid, { customQuery: true, usingDataSelector: usingDataSelector, fromMainGrid: true }); });
            }
            this.mainGrid.element.find(".k-i-refresh").parent().click(this.base.onMainRefreshButtonClick.bind(this.base));
        } catch (exception) {
            kendo.alert("Er is iets fout gegaan tijdens het laden van de data voor deze module. Sluit a.u.b. de module en probeer het nogmaals, of neem contact op met ons.");
            console.error(exception);
            jjl.processing.removeProcess(initialProcess);
        }
    }

    async saveGridViewState(key, event) {
        try {
            const dataToSave = kendo.stringify(event.sender.getOptions().columns);
            sessionStorage.setItem(key, dataToSave);
            await Wiser2.api({
                url: `${this.base.settings.wiserApiRoot}users/grid-settings/${encodeURIComponent(key)}`,
                method: "POST",
                contentType: "application/json",
                data: dataToSave
            });
        } catch (exception) {
            kendo.alert("Er is iets fout gegaan tijdens het opslaan van de instellingen voor dit grid. Probeer het nogmaals, of neem contact op met ons.");
            console.error(exception);
        }
    }

    async loadGridViewState(key, grid) {
        let value;
        
        value = sessionStorage.getItem(key);
        if (!value) {
            value = await Wiser2.api({
                url: `${this.base.settings.wiserApiRoot}users/grid-settings/${encodeURIComponent(key)}`,
                method: "GET",
                contentType: "application/json"
            });

            sessionStorage.setItem(key, value || "[]");
        }

        if (!value) {
            return;
        }

        const columns = JSON.parse(value);
        const gridOptions = grid.getOptions();
        if (!gridOptions || !gridOptions.columns || !gridOptions.columns.length) {
            return;
        }

        for (let column of gridOptions.columns) {
            const savedColumn = columns.filter(c => c.field === column.field);
            if (savedColumn.length === 0) {
                continue;
            }

            column.hidden = savedColumn[0].hidden;
        }

        grid.setOptions(gridOptions);
    }

    async initializeItemsGrid(options, field, loader, itemId, height = undefined, propertyId = 0, extraData = null) {
        // TODO: Implement all functionality of all grids (https://app.asana.com/0/12170024697856/1138392544929161), so that we can use this method for everything.

        try {
            itemId = itemId || this.base.settings.zeroEncrypted;
            let customQueryGrid = options.customQuery === true;
            let kendoGrid;
            options.pageSize = options.pageSize || 25;

            const hideCheckboxColumn = !options.checkboxes || options.checkboxes === "false" || options.checkboxes <= 0;
            const gridOptions = {
                page: 1,
                page_size: options.pageSize,
                skip: 0,
                take: options.pageSize,
                extra_values_for_query: extraData
            };

            if (customQueryGrid) {
                const customQueryResults = await Wiser2.api({
                    url: `${this.base.settings.wiserApiRoot}items/${encodeURIComponent(itemId)}/entity-grids/custom?mode=4&queryId=${options.queryId || this.base.settings.zeroEncrypted}&countQueryId=${options.countQueryId || this.base.settings.zeroEncrypted}`,
                    method: "POST",
                    contentType: "application/json",
                    data: JSON.stringify(gridOptions)
                });

                if (customQueryResults.extra_javascript) {
                    $.globalEval(customQueryResults.extra_javascript);
                }

                if (Wiser2.validateArray(options.columns)) {
                    customQueryResults.columns = options.columns;
                }

                if (!hideCheckboxColumn) {
                    customQueryResults.columns.splice(0, 0, {
                        selectable: true,
                        width: "30px",
                        headerTemplate: "&nbsp;"
                    });
                }

                if (!options.disableOpeningOfItems) {
                    if (customQueryResults.schema_model && customQueryResults.schema_model.fields) {
                        // If there is no field for encrypted ID, don't allow the user to open items, they'd just get an error.
                        options.disableOpeningOfItems = !(customQueryResults.schema_model.fields.encryptedId || customQueryResults.schema_model.fields.encrypted_id || customQueryResults.schema_model.fields.encryptedid || customQueryResults.schema_model.fields.idencrypted);
                    }
                }

                if (!options.hideCommandColumn) {
                    let commandColumnWidth = 0;
                    const commands = [];

                    if (!options.disableOpeningOfItems) {
                        commandColumnWidth += 80;

                        commands.push({
                            name: "openDetails",
                            iconClass: "k-icon k-i-hyperlink-open",
                            text: "",
                            click: (event) => { this.onShowDetailsClick(event, kendoGrid, options); }
                        });
                    }

                    customQueryResults.columns.push({
                        title: "&nbsp;",
                        width: commandColumnWidth,
                        command: commands
                    });
                }

                if (options.allowMultipleRows) {
                    const checkBoxColumns = customQueryResults.columns.filter(c => c.selectable);
                    for (let checkBoxColumn of checkBoxColumns) {
                        delete checkBoxColumn.headerTemplate;
                    }
                }

                kendoGrid = this.generateGrid(field, loader, options, customQueryGrid, customQueryResults, propertyId, height, itemId, extraData);
            } else {
                const gridSettings = await Wiser2.api({
                    url: `${this.base.settings.wiserApiRoot}items/${itemId}/entity-grids/${encodeURIComponent(options.entityType)}?propertyId=${propertyId}&mode=1`,
                    method: "POST",
                    contentType: "application/json",
                    data: JSON.stringify(gridOptions)
                });

                if (gridSettings.extra_javascript) {
                    $.globalEval(gridSettings.extra_javascript);
                }

                // Add most columns here.
                if (gridSettings.columns && gridSettings.columns.length) {
                    for (let i = 0; i < gridSettings.columns.length; i++) {
                        var column = gridSettings.columns[i];

                        switch (column.field || "") {
                            case "":
                                column.hidden = hideCheckboxColumn;
                                if (!options.allowMultipleRows) {
                                    column.headerTemplate = "&nbsp;";
                                }
                                break;
                            case "id":
                                column.hidden = options.hideIdColumn || false;
                                break;
                            case "link_id":
                                column.hidden = options.hideLinkIdColumn || false;
                                break;
                            case "entity_type":
                                column.hidden = options.hideTypeColumn || false;
                                break;
                            case "published_environment":
                                column.hidden = options.hideEnvironmentColumn || false;
                                break;
                            case "name":
                                column.hidden = options.hideTitleColumn || false;
                                break;
                        }
                    }
                }

                if (!options.disableOpeningOfItems) {
                    if (gridSettings.schema_model && gridSettings.schema_model.fields) {
                        // If there is no field for encrypted ID, don't allow the user to open items, they'd just get an error.
                        options.disableOpeningOfItems = !(gridSettings.schema_model.fields.encryptedId || gridSettings.schema_model.fields.encrypted_id || gridSettings.schema_model.fields.encryptedid || gridSettings.schema_model.fields.idencrypted);
                    }
                }

                // Add command columns separately, because of the click event that we can't do properly server-side.
                if (!options.hideCommandColumn) {
                    let commandColumnWidth = 80;
                    let commands = [];

                    if (!options.disableOpeningOfItems) {
                        commands.push({
                            name: "openDetails",
                            iconClass: "k-icon k-i-hyperlink-open",
                            text: "",
                            click: (event) => { this.onShowDetailsClick(event, kendoGrid, options); }
                        });
                    }

                    gridSettings.columns.push({
                        title: "&nbsp;",
                        width: commandColumnWidth,
                        command: commands
                    });
                }

                kendoGrid = this.generateGrid(field, loader, options, customQueryGrid, gridSettings, propertyId, height, itemId, extraData);
            }

        } catch (exception) {
            console.error(exception);
            kendo.alert("Er is iets fout gegaan met het initialiseren van het overzicht. Probeer het a.u.b. nogmaals of neem contact op met ons.");
        }
    }

    generateGrid(element, loader, options, customQueryGrid, data, propertyId, height, itemId, extraData) {
        // TODO: Implement all functionality of all grids (https://app.asana.com/0/12170024697856/1138392544929161), so that we can use this method for everything.
        let isFirstLoad = true;

        const columns = data.columns;
        if (columns && columns.length) {
            for (let column of columns) {
                if (column.field) {
                    column.field = column.field.toLowerCase();
                }

                if (!column.editor) {
                    continue;
                }

                column.editor = this[column.editor];
            }
        }

        const toolbar = [];
        if (!options.toolbar || !options.toolbar.hideClearFiltersButton) {
            toolbar.push({
                name: "clearAllFilters",
                text: "",
                template: `<a class='k-button k-button-icontext clear-all-filters' title='Alle filters wissen' href='\\#' onclick='return window.dynamicItems.grids.onClearAllFiltersClick(event)'><span class='k-icon k-i-filter-clear'></span></a>`
            });
        }

        if (element.data("kendoGrid")) {
            element.data("kendoGrid").destroy();
            element.empty();
        }

        const kendoGrid = element.kendoGrid({
            dataSource: {
                transport: {
                    read: async (transportOptions) => {
                        try {
                            if (loader) {
                                loader.addClass("loading");
                            }

                            if (isFirstLoad) {
                                transportOptions.success(data);
                                isFirstLoad = false;
                                if (loader) {
                                    loader.removeClass("loading");
                                }
                                return;
                            }

                            if (!transportOptions.data) {
                                transportOptions.data = {};
                            }
                            transportOptions.data.extra_values_for_query = extraData;
                            transportOptions.data.page_size = transportOptions.data.pageSize;

                            if (customQueryGrid) {
                                const customQueryResults = await Wiser2.api({
                                    url: `${this.base.settings.wiserApiRoot}items/${itemId}/entity-grids/custom?mode=4&queryId=${options.queryId || this.base.settings.zeroEncrypted}&countQueryId=${options.countQueryId || this.base.settings.zeroEncrypted}`,
                                    method: "POST",
                                    contentType: "application/json",
                                    data: JSON.stringify(transportOptions.data)
                                });

                                transportOptions.success(customQueryResults);

                                if (loader) {
                                    loader.removeClass("loading");
                                }
                            } else {
                                const gridSettings = await Wiser2.api({
                                    url: `${this.base.settings.wiserApiRoot}items/${itemId}/entity-grids/${encodeURIComponent(options.entityType)}?propertyId=${propertyId}&mode=1`,
                                    method: "POST",
                                    contentType: "application/json",
                                    data: JSON.stringify(transportOptions.data)
                                });

                                transportOptions.success(gridSettings);

                                if (loader) {
                                    loader.removeClass("loading");
                                }
                            }
                        } catch (exception) {
                            console.error(exception);
                            if (loader) {
                                loader.removeClass("loading");
                            }
                            kendo.alert("Er is iets fout gegaan tijdens het laden van het veld '{title}'. Probeer het a.u.b. nogmaals door de pagina te verversen, of neem contact op met ons.");
                            transportOptions.error(exception);
                        }
                    }
                },
                serverPaging: true,
                serverSorting: true,
                serverFiltering: true,
                pageSize: options.pageSize || 10,
                schema: {
                    data: "data",
                    total: "total_results",
                    model: data.schema_model
                }
            },
            columns: columns,
            pageable: {
                pageSize: options.pageSize || 10,
                refresh: true
            },
            toolbar: toolbar,
            sortable: true,
            resizable: true,
            editable: false,
            navigatable: true,
            selectable: options.selectable || false,
            height: height,
            filterable: {
                extra: false,
                operators: {
                    string: {
                        startswith: "Begint met",
                        eq: "Is gelijk aan",
                        neq: "Is ongelijk aan",
                        contains: "Bevat",
                        doesnotcontain: "Bevat niet",
                        endswith: "Eindigt op"
                    }
                },
                messages: {
                    isTrue: "<span>Ja</span>",
                    isFalse: "<span>Nee</span>"
                }
            },
            filterMenuInit: this.base.grids.onFilterMenuInit.bind(this),
            filterMenuOpen: this.base.grids.onFilterMenuOpen.bind(this)
        }).data("kendoGrid");

        kendoGrid.thead.kendoTooltip({
            filter: "th",
            content: function (event) {
                const target = event.target; // element for which the tooltip is shown
                return $(target).text();
            }
        });

        if (!options.disableOpeningOfItems) {
            element.on("dblclick", "tbody tr[data-uid] td", (event) => { this.onShowDetailsClick(event, kendoGrid, options); });
        }

        if (!options.allowMultipleRows) {
            kendoGrid.tbody.on("click", ".k-checkbox", (event) => {
                var row = $(event.target).closest("tr");

                if (row.hasClass("k-state-selected")) {
                    setTimeout(() => {
                        kendoGrid.clearSelection();
                    });
                } else {
                    kendoGrid.clearSelection();
                }
            });
        }

        return kendoGrid;
    }

    /**
     * This method adds a counter to a grid.
     * This counter shows on the bottom right of the grid how many rows are selected.
     * @param {any} gridElement The div that contains the grid.
     */
    attachSelectionCounter(gridElement) {
        const onSelectionChange = () => {
            const num = $(gridElement).data("kendoGrid").select().length;
            const numSelectedElement = gridElement.querySelector(".numSelected");
            const newTxt = `${num} item${(num === 1 ? "" : "s")} geselecteerd`;

            if (numSelectedElement) {
                numSelectedElement.innerHTML = newTxt;
                if (num === 0) {
                    $(numSelectedElement).hide();
                } else {
                    $(numSelectedElement).show();
                }
            } else if (num > 0) {
                const pagerInfoElement = gridElement.querySelector(".k-pager-info");
                if (pagerInfoElement) {
                    const newEl = `<span class="k-pager-info k-label numSelected">${newTxt}</span>`;
                    pagerInfoElement.insertAdjacentHTML("afterEnd", newEl);
                }
            }
        };

        $(gridElement).data("kendoGrid").bind("change", onSelectionChange);
    }

    /**
     * Event to show the details of an item from a sub entities grid.
     * @param {any} event The event.
     * @param {any} grid The grid that executed the event.
     * @param {any} options The options for the grid.
     */
    async onShowDetailsClick(event, grid, options) {
        event.preventDefault();

        const dataItem = grid.dataItem($(event.currentTarget).closest("tr"));
        const tableCell = $(event.currentTarget).closest("td");
        const column = grid.options.columns[tableCell.index()] || {};

        let itemId = dataItem.id || dataItem.itemId || dataItem.itemid || dataItem.item_id;
        let encryptedId = dataItem.encryptedId || dataItem.encrypted_id || dataItem.encryptedid || dataItem.idencrypted;
        const originalEncryptedId = encryptedId;
        let entityType = dataItem.entity_type;
        let title = dataItem.title;
        const linkId = dataItem.link_id;

        if (options.fromMainGrid && this.base.settings.openGridItemsInBlock) {
            this.base.grids.informationBlockIframe.attr("src", `${"/Modules/DynamicItems"}?itemId=${encryptedId}&moduleId=${this.base.settings.moduleId}&iframe=true`);
            return;
        }

        // If this grid uses a custom query, it means that we need to get the data a different way, because the grid can have data from multiple different entity types.
        if (options.customQuery === true) {
            if (!column.field) {
                // If the clicked column has no field property (such as the command column), use the item ID of the main entity type.
                itemId = dataItem[`ID_${options.entityType || entityType}`] || dataItem[`id_${options.entityType || entityType}`] || dataItem[`itemId_${options.entityType || entityType}`] || dataItem[`itemid_${options.entityType || entityType}`] || dataItem[`item_id_${options.entityType || entityType}`] || itemId;
                encryptedId = dataItem[`encryptedId_${options.entityType || entityType}`] || dataItem[`encryptedid_${options.entityType || entityType}`] || dataItem[`encrypted_id_${options.entityType || entityType}`] || dataItem[`idencrypted_${options.entityType || entityType}`] || encryptedId;
            } else if (!options.usingDataSelector) {
                // If the clicked column has a field property, it should contain the entity name. Then we can find the ID column for that same entity.
                const split = column.field.split(/_(.+)/).filter(s => s !== "");
                if (split.length < 2 && !entityType) {
                    if (!options.hideCommandColumn && (!this.base.settings.gridViewSettings || !this.base.settings.gridViewSettings.hideCommandColumn)) {
                        console.error(`Could not retrieve entity type from clicked column ('${column.field}')`);
                        kendo.alert("Er is geen entiteittype gevonden voor de aangeklikte kolom. Neem a.u.b. contact op met ons.");
                    }

                    return;
                }

                let idFound = false;
                let encryptedIdFound = false;
                if (split.length >= 2) {
                    entityType = split[split.length - 1];

                    for (const key in dataItem) {
                        if (!dataItem.hasOwnProperty(key)) {
                            continue;
                        }

                        if (!idFound && (key.indexOf(`ID_${entityType}`) === 0 || key.indexOf(`id_${entityType}`) === 0 || key.indexOf(`itemId_${entityType}`) === 0 || key.indexOf(`itemid_${entityType}`) === 0 || key.indexOf(`item_id_${entityType}`) === 0)) {
                            itemId = dataItem[key];
                            idFound = true;
                        }

                        if (!encryptedIdFound && (key.indexOf(`encryptedId_${entityType}`) === 0 || key.indexOf(`encryptedid_${entityType}`) === 0 || key.indexOf(`encrypted_id_${entityType}`) === 0 || key.indexOf(`idencrypted_${entityType}`) === 0)) {
                            encryptedId = dataItem[key];
                            encryptedIdFound = true;
                        }

                        if (encryptedIdFound && idFound) {
                            break;
                        }
                    }
                }
            }

            if (!encryptedId) {
                encryptedId = originalEncryptedId;
            }

            if (!encryptedId) {
                if (!options.hideCommandColumn && (!this.base.settings.gridViewSettings || !this.base.settings.gridViewSettings.hideCommandColumn)) {
                    kendo.alert("Er is geen encrypted ID gevonden. Neem a.u.b. contact op met ons.");
                }
                return;
            }

            if (!title || !itemId || !entityType) {
                const itemDetails = (await this.base.getItemDetails(encryptedId))[0];
                if (!itemDetails) {
                    kendo.alert("Er is geen item gevonden met het id in de geselecteerde regel. Waarschijnlijk is dit geen geldig ID. Neem a.u.b. contact op met ons.");
                    return;
                }

                title = title || itemDetails.title;
                itemId = itemId || itemDetails.id || itemDetails.itemId || itemDetails.itemId;
                entityType = entityType || itemDetails.entity_type;
            }
        }

        if (!encryptedId) {
            kendo.alert("Er is geen encrypted ID gevonden. Neem a.u.b. contact op met ons.");
            return;
        }

        this.base.windows.loadItemInWindow(false, itemId, encryptedId, entityType, title, !options.hideTitleFieldInWindow, grid, options, linkId);
    }

    /**
     * Event that gets fired when clicking the link sub entity button in a sub-entities-grid.
     * This will open a window with a grid that contains all entities of a certain type.
     * The user can use checkboxes in that grid to link items.
     * @param {any} encryptedParentId The encrypted item ID of the parent to link the items to.
     * @param {any} plainParentId The plain item ID of the parent to link the items to.
     * @param {any} entityType The entity type of items to show in the search window.
     * @param {any} senderGridSelector A selector to find the sender grid.
     * @param {any} linkTypeNumber The link type number.
     * @param {boolean} hideIdColumn Indicates whether or not to hide the ID column.
     * @param {boolean} hideLinkIdColumn Indicates whether or not to hide the link ID column.
     * @param {boolean} hideTypeColumn Indicates whether or not to hide the type column.
     * @param {boolean} hideEnvironmentColumn Indicates whether or not to hide the environment column.
     * @param {boolean} hideTitleColumn Indicates whether or not to hide the title column.
     * @param {number} propertyId The ID of the current property.
     * @param {any} gridOptions The options of the grid.
     */
    onLinkSubEntityClick(encryptedParentId, plainParentId, entityType, senderGridSelector, linkTypeNumber, hideIdColumn, hideLinkIdColumn, hideTypeColumn, hideEnvironmentColumn, hideTitleColumn, propertyId, gridOptions) {
        linkTypeNumber = linkTypeNumber || "";
        if (typeof gridOptions === "string") {
            gridOptions = JSON.parse(gridOptions);
        }

        this.base.windows.searchItemsWindow.maximize().open();
        this.base.windows.searchItemsWindow.title(`${entityType} zoeken en koppelen`);
        this.base.windows.initializeSearchItemsGrid(entityType, encryptedParentId, propertyId, gridOptions);
        $.extend(this.base.windows.searchItemsWindowSettings, {
            parentId: encryptedParentId,
            plainParentId: plainParentId,
            senderGrid: $(senderGridSelector).data("kendoGrid"),
            entityType: entityType,
            linkTypeNumber: linkTypeNumber,
            propertyId: propertyId,
            currentItemIsSourceId: gridOptions.currentItemIsSourceId
        });
        $.extend(this.base.windows.searchGridSettings, {
            hideIdColumn: hideIdColumn,
            hideLinkIdColumn: hideLinkIdColumn,
            hideTypeColumn: hideTypeColumn,
            hideEnvironmentColumn: hideEnvironmentColumn,
            hideTitleColumn: hideTitleColumn,
            propertyId: propertyId,
            enableSelectAllServerSide: gridOptions.searchGridSettings.enableSelectAllServerSide,
            currentItemIsSourceId: gridOptions.currentItemIsSourceId
        });
    }

    /**
     * Event that gets executed when the user clicks the button to create a new sub entity inside a sub-entities-grid.
     * This will create a new item and then open a popup where the user can edit the values with.
     * @param {string} parentId The encrypted ID of the parent to link the item to. This is usually the item that contains the sub-entities-grid.
     * @param {string} entityType The type of entity to create.
     * @param {string} senderGridSelector The CSS selector to find the main element for the sub-entities-grid.
     * @param {boolean} showTitleField Whether or not to show the field where the user can edit the title/name of the new item.
     * @param {number} linkTypeNumber The link type number.
     */
    async onNewSubEntityClick(parentId, entityType, senderGridSelector, showTitleField, linkTypeNumber) {
        linkTypeNumber = linkTypeNumber || "";

        const senderGrid = $(senderGridSelector).data("kendoGrid");
        if (senderGrid) {
            senderGrid.element.siblings(".grid-loader").addClass("loading");
        }

        try {
            // Create the new item.
            const createItemResult = await this.base.createItem(entityType, parentId, "", linkTypeNumber);
            if (createItemResult) {
                await this.base.windows.loadItemInWindow(true, createItemResult.itemIdPlain, createItemResult.itemId, entityType, null, showTitleField, senderGrid, { hideTitleColumn: !showTitleField }, createItemResult.linkId, `Nieuw(e) ${entityType} aanmaken`);
            }
        } catch (exception) {
            console.error(exception);
            let error = exception;
            if (exception.responseText) {
                error = exception.responseText;
            } else if (exception.statusText) {
                error = exception.statusText;
            }
            kendo.alert(`Er is iets fout gegaan met het aanmaken van het item. Probeer het a.u.b. nogmaals of neem contact op met ons.<br><br>De fout was:<br><pre>${kendo.htmlEncode(error)}</pre>`);
        }

        if (senderGrid) {
            senderGrid.element.siblings(".grid-loader").removeClass("loading");
        }
    }

    /**
     * Event for deleting an item and/or item link.
     * It gets executed when the user clicks the delete button in a sub entities grid.
     * @param {any} event The click event of the delete button.
     * @param {any} senderGrid The sender grid.
     * @param {string} deletionType The deletion type. Possible values: "askUser", "deleteItem" or "deleteLink".
     * @param {any} options The options of the field/property.
     */
    async onDeleteItemClick(event, senderGrid, deletionType, options) {
        deletionType = deletionType || "";
        // prevent page scroll position change
        event.preventDefault();
        // e.target is the DOM element representing the button
        const tr = $(event.target).closest("tr"); // get the current table row (tr)
        // get the data bound to the current table row
        const dataItem = senderGrid.dataItem(tr);
        let encryptedId = dataItem.encryptedId || dataItem.encrypted_id || dataItem.encryptedid;

        if (!encryptedId) {
            // If the clicked column has no field property (such as the command column), use the item ID of the main entity type.
            const itemId = dataItem[`ID_${options.entityType}`] || dataItem[`id_${options.entityType}`] || dataItem[`itemId_${options.entityType}`] || dataItem[`itemid_${options.entityType}`] || dataItem[`item_id_${options.entityType}`];

            if (!itemId) {
                kendo.alert(`Er is geen encrypted ID gevonden voor dit item. Neem a.u.b. contact op met ons.`);
                return;
            }

            const itemDetails = (await this.base.getItemDetails(itemId))[0];
            encryptedId = itemDetails.encryptedId || itemDetails.encrypted_id || itemDetails.encryptedid;
        }

        switch (deletionType.toLowerCase()) {
            case "askuser":
                {
                    const dialog = $("#gridDeleteDialog");

                    dialog.kendoDialog({
                        title: "Verwijderen",
                        closable: false,
                        modal: true,
                        content: "<p>Wilt u het gehele item verwijderen, of alleen de koppeling tussen de 2 items?</p>",
                        deactivate: (e) => {
                            // Destroy the dialog on deactivation so that it can be re-initialized again next time.
                            // If we don't do this, deleting multiple items in a row will not work properly.
                            e.sender.destroy();
                        },
                        actions: [
                            {
                                text: "Annuleren"
                            },
                            {
                                text: "Gehele item",
                                action: (e) => {
                                    try {
                                        this.base.deleteItem(encryptedId, options.entityType).then(() => {
                                            senderGrid.dataSource.read();
                                        });
                                    } catch (exception) {
                                        console.error(exception);
                                        if (exception.status === 409) {
                                            const message = exception.responseText || "Het is niet meer mogelijk om dit item te verwijderen.";
                                            kendo.alert(message);
                                        } else {
                                            kendo.alert("Er is iets fout gegaan tijdens het verwijderen van dit item. Probeer het a.u.b. nogmaals of neem contact op met ons.");
                                        }
                                    }
                                }
                            },
                            {
                                text: "Alleen koppeling",
                                primary: true,
                                action: (e) => {
                                    const destinationItemId = dataItem.encrypted_destination_item_id || senderGrid.element.closest(".item").data("itemIdEncrypted");
                                    this.base.removeItemLink(options.currentItemIsSourceId ? destinationItemId : encryptedId, options.currentItemIsSourceId ? encryptedId : destinationItemId, dataItem.link_type_number).then(() => {
                                        senderGrid.dataSource.read();
                                    });
                                }
                            }
                        ]
                    }).data("kendoDialog").open();

                    break;
                }
            case "deleteitem":
                {
                    if (!options || options.showDeleteConformations !== false) {
                        await kendo.confirm("Weet u zeker dat u dit item wilt verwijderen?");
                    }

                    try {
                        await this.base.deleteItem(dataItem.encryptedId || dataItem.encrypted_id || dataItem.encryptedid, options.entityType);
                    } catch (exception) {
                        console.error(exception);
                        if (exception.status === 409) {
                            const message = exception.responseText || "Het is niet meer mogelijk om dit item te verwijderen.";
                            kendo.alert(message);
                        } else {
                            kendo.alert("Er is iets fout gegaan tijdens het verwijderen van dit item. Probeer het a.u.b. nogmaals of neem contact op met ons.");
                        }
                    }
                    senderGrid.dataSource.read();
                    break;
                }
            case "deletelink":
                {
                    if (!options || options.showDeleteConformations !== false) {
                        await kendo.confirm("Weet u zeker dat u de koppeling met dit item wilt verwijderen? Let op dat alleen de koppeling wordt verwijderd, niet het item zelf.");
                    }

                    const destinationItemId = dataItem.encrypted_destination_item_id || senderGrid.element.closest(".item").data("itemIdEncrypted");
                    await this.base.removeItemLink(options.currentItemIsSourceId ? destinationItemId : encryptedId, options.currentItemIsSourceId ? encryptedId : destinationItemId, dataItem.link_type_number);
                    senderGrid.dataSource.read();
                    break;
                }
            default:
                {
                    console.warn(`onGridDeleteItemClick with unsupported deletionType '${deletionType}'`);
                    break;
                }
        }
    }

    onItemLinkerSelectAll(treeViewSelector, checkAll) {
        const treeView = $(treeViewSelector);

        kendo.confirm("Weet u zeker dat u alles wilt aan- of uitvinken? Indien dit veel items zijn kan dit lang duren.").then(() => {
            const allCheckBoxes = treeView.find(".k-checkbox-wrapper input");
            allCheckBoxes.prop("checked", checkAll).trigger("change");
        });
    }

    /**
     * This is for handling the event 'filterMenuInit' in a Kendo grid.
     * It will set the formatting of numeric fields and maybe other things in the future.
     * @param {any} event The kendo event.
     */
    onFilterMenuInit(event) {
        // Set the format of numeric fields, otherwise numbers will be shown like '3.154.079,00' instead of '3154079'.
        event.container.find("[data-role='numerictextbox']").each((index, element) => {
            $(element).data("kendoNumericTextBox").setOptions({
                format: "0"
            });
        });
    }

    /**
     * This is for handling the event 'filterMenuOpen' in a Kendo grid.
     * @param {any} event The kendo event.
     */
    onFilterMenuOpen(event) {
        // Set the focus on the last textbox in the filter menu.
        event.container.find(".k-textbox:visible, .k-input:visible").last().focus();
    }

    /**
     * Event handler for clicking the clear all filters button in a grid.
     * @param {any} event The click event.
     */
    onClearAllFiltersClick(event) {
        event.preventDefault();

        const grid = $(event.target).closest(".k-grid").data("kendoGrid");
        if (!grid) {
            console.error("Grid not found, cannot clear filters.", event, $(event.target).closest(".k-grid"));
            return;
        }

        grid.dataSource.filter({});
    }

    timeEditor(container, options) {
        $(`<input data-text-field="${options.field}" data-value-field="${options.field}" data-bind="value:${options.field}" data-format="${options.format}"/>`)
            .appendTo(container)
            .kendoTimePicker({});
    }

    dateTimeEditor(container, options) {
        $(`<input data-text-field="${options.field}" data-value-field="${options.field}" data-bind="value:${options.field}" data-format="${options.format}"/>`)
            .appendTo(container)
            .kendoDateTimePicker({});
    }

    booleanEditor(container, options) {
        const guid = kendo.guid();

        $(`<label class="checkbox"><input type="checkbox" id="${guid}" class="textField k-input" name="${options.field}" data-type="boolean" data-bind="checked:${options.field}" /><span></span></label>`)
            .appendTo(container);
    }
}