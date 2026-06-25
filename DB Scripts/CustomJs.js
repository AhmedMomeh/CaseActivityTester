let domain = document.location.origin.toLowerCase();
let url = document.URL.toLowerCase();
let CustomCaseApiUrl = "https://caseservicedev.unioncoop.ae";
let caseportalURL = "http://localhost:33333";
let IAMURL = "http://localhost:11111";
let JDEURL = "https://localhost:7042/jderest/orchestrator";

// Expose these  on `window` so Form.io form JSON (JDEURL) can reference them as window.JDEURL
window.JDEURL = JDEURL;
window.JDEAuthHeader = "Basic SkRFT1JDSDp1Y2pkZTEyMw==";   // JDEORCH:ucjde123

// ---------------------------------------------------------------------------
// JDE Loader — shows a spinner overlay during any HTTP call to JDE so slow
// ---------------------------------------------------------------------------
(function attachJdeLoader() {
    if (window.__jdeLoaderInstalled) return;
    window.__jdeLoaderInstalled = true;

    var inFlight = 0;
    var maskName = "jde-loading-mask";

    function isJdeUrl(u) {
        if (typeof u !== 'string') return false;
        return u.indexOf('/jderest/orchestrator') >= 0 ||
               u.indexOf('jdeproxy')              >= 0;
    }

    // ----- Self-contained CSS overlay (used when Common.mask isn't loaded) ---
    function showFallback() {
        if (!document.getElementById('jde-loader-css')) {
            var s = document.createElement('style');
            s.id = 'jde-loader-css';
            s.textContent =
                '#jde-loader-overlay{position:fixed;inset:0;background:rgba(0,0,0,.35);z-index:2147483647;display:flex;align-items:center;justify-content:center;}' +
                '#jde-loader-overlay .jde-spinner{width:54px;height:54px;border:6px solid rgba(255,255,255,.25);border-top-color:#fff;border-radius:50%;animation:jde-spin .9s linear infinite;}' +
                '@keyframes jde-spin{to{transform:rotate(360deg)}}';
            (document.head || document.documentElement).appendChild(s);
        }
        if (document.getElementById('jde-loader-overlay')) return;
        var ov = document.createElement('div');
        ov.id = 'jde-loader-overlay';
        ov.innerHTML = '<div class="jde-spinner"></div>';
        document.body.appendChild(ov);
    }
    function hideFallback() {
        var ov = document.getElementById('jde-loader-overlay');
        if (ov && ov.parentNode) ov.parentNode.removeChild(ov);
    }

    function showLoader() {
        if (inFlight++ > 0) return;
        if (typeof Common !== 'undefined' && Common.mask) {
            try { Common.mask(document.body, maskName); return; } catch (e) {}
        }
        showFallback();
    }
    function hideLoader() {
        if (--inFlight > 0) return;
        inFlight = 0;
        if (typeof Common !== 'undefined' && Common.unmask) {
            try { Common.unmask(maskName); } catch (e) {}
        }
        hideFallback();
    }

    // 1) XMLHttpRequest — primary transport for Form.io URL dataSrc.
    //    loadend fires for success / error / abort / timeout, so it's the
    //    only event we need.
    var XO = XMLHttpRequest.prototype.open;
    var XS = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function (method, url) {
        this.__jdeMatched = isJdeUrl(url);
        return XO.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function () {
        if (this.__jdeMatched) {
            showLoader();
            this.addEventListener('loadend', hideLoader);
        }
        return XS.apply(this, arguments);
    };

    // 2) fetch.
    if (typeof window.fetch === 'function') {
        var origFetch = window.fetch;
        window.fetch = function (input) {
            var u = (typeof input === 'string') ? input : (input && input.url);
            if (!isJdeUrl(u)) return origFetch.apply(this, arguments);
            showLoader();
            return Promise.resolve(origFetch.apply(this, arguments)).finally(hideLoader);
        };
    }

    // 3) jQuery $.ajax — safety net for calls made through jQuery in this
    //    file. Pairs with the XHR patch (+2/-2 → still nets to zero).
    if (window.jQuery) {
        $(document).ajaxSend(function (e, x, s)     { if (isJdeUrl(s && s.url)) showLoader(); });
        $(document).ajaxComplete(function (e, x, s) { if (isJdeUrl(s && s.url)) hideLoader(); });
    }

    // 4) Formio.makeRequest — Formio may load after this script, so poll.
    var tries = 0;
    (function patchFormio() {
        if (typeof Formio !== 'undefined' && Formio.makeRequest && !Formio.__jdeLoaderPatched) {
            var origMR = Formio.makeRequest;
            Formio.makeRequest = function (formio, type, url) {
                if (!isJdeUrl(url)) return origMR.apply(this, arguments);
                showLoader();
                return Promise.resolve(origMR.apply(this, arguments)).finally(hideLoader);
            };
            Formio.__jdeLoaderPatched = true;
            return;
        }
        if (tries++ < 100) setTimeout(patchFormio, 100);
    })();
})();

$(document).ready(function () {

    // Run the IAM + JDE preload ONLY when the page actually has a form that
  
    attachJdeAutoBootstrap();

    // loadTaskhistory in Application metadata
    $(document).ajaxSuccess(function (event, xhr, settings) {
        if (settings.url.includes("Document/GetDocumentByTaskId")) {
            loadTaskhistory();
        }

        if (settings.url.includes("File/ListByDocumentId")) {
            window.addEventListener('contextmenu', (event) => {
                $('li:contains("Replace")').hide();
            });
        }

        // Initiation Forms never call these endpoints — the Portal hits them
        // only when opening an EXISTING document/task page or the Application
        // Metadata view. So if either URL fires we know we're NOT in the
        // initiation case, and step-specific panels can be shown.
        if (settings.url.includes("Document/GetDocumentByTaskId") ||
            settings.url.includes("Document/GetDocumentTasks") ||
            settings.url.includes("Document/GetClosedDocument")) {
            window.IsExistingCaseView = true;

            // Push the flag into the form so any panel keyed to
            // data.IsExistingCaseView re-evaluates its customConditional.
            if (typeof Formio !== 'undefined' && Formio.forms) {
                Object.values(Formio.forms).forEach(function (f) {
                    if (typeof f.getComponent !== 'function') return;
                    var fld = f.getComponent('IsExistingCaseView');
                    if (fld) fld.setValue(true);
                });
            }
        }   

    });


});

// ---------------------------------------------------------------------------
// loadCurrentUserFromIAM(callback)
// ---------------------------------------------------------------------------
function loadCurrentUserFromIAM(callback) {
    // Cache: once per page, every later caller reuses the same email.
    if (window.CurrentUserEmail) {
        if (typeof callback === 'function') callback(window.CurrentUserEmail);
        return;
    }

    var userId = $('#hdUserId').val();
    var token  = window.IdentityAccessToken;

    if (!userId) {
        console.warn('loadCurrentUserFromIAM: #hdUserId is empty — not in a Portal-authenticated page yet');
        return;
    }
    if (!token) {
        console.error('loadCurrentUserFromIAM: window.IdentityAccessToken is missing');
        return;
    }

    $.ajax({
        url: `${IAMURL}/Api/GetUser?id=${userId}`,
        method: 'GET',
        headers: {
            'Accept'        : 'application/json',
            'Authorization' : `Bearer ${token}`
        },
        success: function (userData) {
            var email = (userData && (userData.email || userData.Email || userData.userEmail)) || '';
            window.CurrentUserEmail = email;
            if (typeof callback === 'function') callback(email);
        },
        error: function (xhr, status, error) {
            console.error('IAM GetUser failed:', status, error, xhr.responseText);
        }
    });
}

// ---------------------------------------------------------------------------
// loadIsHRFromJDE(callback)
// ---------------------------------------------------------------------------
function loadIsHRFromJDE(callback) {
    // Cache: once per page, every later caller reuses the same IsHR / DepartmentCode.
    if (typeof window.IsHR !== 'undefined') {
        if (typeof callback === 'function') callback(window.IsHR);
        return;
    }

    // Read the email already cached by loadCurrentUserFromIAM. The caller
    // is responsible for ensuring that runs first (the $(document).ready
    // chain at the top of this file does exactly that).
    var email = window.CurrentUserEmail;
    if (!email) {
        console.warn('loadIsHRFromJDE: window.CurrentUserEmail not set yet — call loadCurrentUserFromIAM first.');
        return;
    }

    $.ajax({
        url: `${JDEURL}/GetEmployeeInfoByEmail`,
        method: 'POST',
        contentType: 'application/json',
        headers: {
            'Accept'        : 'application/json',
            'Authorization' : 'Basic SkRFT1JDSDp1Y2pkZTEyMw=='   // JDEORCH:ucjde123
        },
        data: JSON.stringify({ Email: email }),
        success: function (emp) {
            window.IsHR              = !!(emp && emp.IsHR);
            window.EmpDepartmentCode = (emp && emp.DepartmentCode) || '';
            window.EmpName = (emp && emp.Name) || '';
            window.EmpJobDesc = (emp && emp.JobDesc) || '';
            if (typeof callback === 'function') callback(window.IsHR);
        },
        error: function (xhr, status, error) {
            console.error('JDE GetEmployeeInfoByEmail failed:', status, error, xhr.responseText);
        }
    });
}


// ---------------------------------------------------------------------------
// attachJdeAutoBootstrap()
// ---------------------------------------------------------------------------
function attachJdeAutoBootstrap() {
    if (window.__jdeBootstrapWatcherInstalled) return;
    window.__jdeBootstrapWatcherInstalled = true;
  
    var BOOTSTRAP_NEEDLES = /\/jderest\/orchestrator|jdeproxy|window\.JDEURL|window\.IsHR|window\.CurrentUserEmail|window\.Emp[A-Za-z]+/;

    var fired = false;
    function maybeFireFor(formInstance) {
        if (fired) return;
        try {
            // Form.io exposes the loaded schema as instance.form (and on
            // some builds as instance.schema). Either is fine — we just
            // need a JSON-serializable shape to scan.
            var schema = formInstance && (formInstance.form || formInstance.schema);
            if (!schema) return;
            var s = JSON.stringify(schema);
            if (!BOOTSTRAP_NEEDLES.test(s)) return;
            fired = true;
            runJdeBootstrap();
        } catch (e) {
            console.warn('JDE auto-bootstrap detect failed:', e);
        }
    }

    function runJdeBootstrap() {
        loadCurrentUserFromIAM(function (email) {
            if (!email) return;
            loadIsHRFromJDE(function () {
                // Globals are now set — re-evaluate any customConditional
                // / customDefaultValue that referenced them before they
                // existed. redraw() is the lightest hook that covers both.
                if (typeof Formio !== 'undefined' && Formio.forms) {
                    Object.values(Formio.forms).forEach(function (f) {
                        try { if (f && f.redraw) f.redraw(); } catch (e) {}
                    });
                }
            });
        });
    }

    // 1) Forms loaded AFTER we install: hook Formio's formLoad event.
    if (typeof Formio !== 'undefined' && Formio.events && typeof Formio.events.on === 'function') {
        try { Formio.events.on('formLoad', maybeFireFor); } catch (e) {}
    }

    // 2) Forms already rendered (or builds without 'formLoad'): poll
    //    Formio.forms briefly. Bails out as soon as we fire OR after a
    //    short window — keeps the page quiet on non-form views.
    var tries = 0;
    (function watchForms() {
        if (fired) return;
        if (typeof Formio !== 'undefined' && Formio.forms) {
            Object.values(Formio.forms).forEach(maybeFireFor);
        }
        if (fired) return;
        if (tries++ < 100) setTimeout(watchForms, 100);   // ~10 s total
    })();
}


function loadTaskhistory() {
    const taskId = $('#hdId').val();
    const documentId = $('#hdDocumentId').val();
    const documentTasksUrl = `${caseportalURL}/Document/GetDocumentTasks?taskId=${taskId}&documentId=${documentId}&fromSearch=false`;
    const accessToken = window.IdentityAccessToken;

    if (!accessToken) {
        console.error("No access token found in window.IdentityAccessToken");
        return;
    }

    $.ajax({
        url: documentTasksUrl,
        method: "GET",
        headers: {
            "Authorization": `Bearer ${accessToken}`
        },
        success: function (taskData) {
            if (taskData && taskData.length > 0) {
                let tasksContainer = $('#builder .tasks-container');
                if (tasksContainer.length === 0) {
                    $('#builder').append('<h3>Recommendation</h3><div class="tasks-container"></div>');
                    tasksContainer = $('#builder .tasks-container');
                }

                tasksContainer.empty();

                // Create an array of promises for fetching user data
                const promises = taskData.map((task, index) => {
                    const getUserUrl = `${IAMURL}/api/GetUser?id=${task.ownerUserId}&language=en`;

                    return $.ajax({
                        url: getUserUrl,
                        method: "GET",
                        headers: {
                            "Authorization": `Bearer ${accessToken}`
                        }
                    }).then(userData => {
                        const ownerName = userData.fullName;
                        const panelId = "customPanel_" + task.id + "_" + index;
                        const bodyId = "customBody_" + task.id + "_" + index;

                        return `
<div class="panel b">
<div class="panel-heading load-panel" style="cursor: pointer;" data-panelid="${panelId}" data-taskid="${task.id}">
<h4 class="panel-title">
<div class="pull-right">
<em class="fa fa-check text-success mr-lg" title="Completed"></em>
<em class="fa fa-envelope-open-o text-info mr-lg" title="Read"></em>
<span class="label text-right" style="padding:3px;background-color:#27c24c">For Approval</span>
</div>
<small><i class="customPlusIcon fa fa-plus"></i></small>
<span title="Task date">${task.taskDate}</span>
<span title="Owner"> - ${ownerName}</span>
</h4>
</div>
<div class="panel-collapse collapse" id="${panelId}">
<div class="panel-body" id="${bodyId}">
<!-- Task Details Here -->
</div>
</div>
</div>
                        `;
                    });
                });

                // Wait for all promises to complete and append them in order
                Promise.all(promises).then(panels => {
                    tasksContainer.append(panels.join(""));
                }).catch(error => {
                    console.error("Error loading user data:", error);
                });

                // Ensure single event delegation
                $('#builder .tasks-container').off('click', '.panel-heading').on('click', '.panel-heading', function () {
                    const panelId = $(this).data('panelid');
                    const currentTaskId = $(this).data('taskid');
                    const currentPanelBody = $(`#${panelId}`);
                    const icon = $(this).find('small i.customPlusIcon');

                    $('#builder .panel-collapse').not(currentPanelBody).addClass('collapse');
                    $('#builder .customPlusIcon').not(icon).removeClass('fa-minus').addClass('fa-plus');

                    currentPanelBody.toggleClass('collapse');

                    if (currentPanelBody.hasClass('collapse')) {
                        icon.removeClass('fa-minus').addClass('fa-plus');
                    } else {
                        icon.removeClass('fa-plus').addClass('fa-minus');
                    }

                    fetchTaskData(currentTaskId, currentPanelBody);
                });
            } else {
                console.log("No task history available.");
            }
        },
        error: function () {
            console.error("Failed to fetch document tasks. Unauthorized access.");
        }
    });
}

function fetchTaskData(currentTaskId, panelBody) {
    const taskId = $('#hdId').val();
    const documentId = $('#hdDocumentId').val();
    const accessToken = window.IdentityAccessToken;

    const apiUrl = `${caseportalURL}/Task/GetTaskForm?currentTaskId=${currentTaskId}&fromSearch=false&taskId=${taskId}&documentId=${documentId}`;

    // Fetch data from the API
    $.ajax({
        url: apiUrl,
        method: "GET",
        headers: {
            "Authorization": `Bearer ${accessToken}`
        },
        success: function (data) {
            // Parse the response data
            const formDesigner = JSON.parse(data.formDesigner);
            const formDesignerTranslation = JSON.parse(data.formDesignerTranslation);
            const formData = JSON.parse(data.formData);

            // Process the form designer and form data here
            bindFormData(formDesigner, formDesignerTranslation, formData, panelBody);
        },
        error: function (error) {
            console.error('Error with API request:', error);
        }
    });
}

function bindFormData(formDesigner, formDesignerTranslation, formData, panelBody) {
    // Iterate over the form components and bind them to the DOM
    formDesigner.components.forEach(function (component) {
        // Only process the textarea components
        if (component.type === 'textarea') {
            // Translate the label using formDesignerTranslation
            const labelTranslation = formDesignerTranslation.find(t => t.Keyword === component.label);
            const translatedLabel = labelTranslation ? labelTranslation.Ar : component.label;  // Default to the original label if translation is not found

            // Create a new element for the textarea
            const componentElement = createComponentElement(component, translatedLabel, formData);

            // Check if the component is already appended (optional check for textarea only)
            if (panelBody.find('textarea[name="' + component.key + '"]').length === 0) {
                // Append the element to the panel only if it's not already appended
                panelBody.append(componentElement);
            }
        }
    });
}

function createComponentElement(component, translatedLabel, formData) {
    const element = $('<div>').addClass('form-component');

    // Set label with inline CSS for better presentation
    const label = $('<label>')
        .text(translatedLabel)
        .css({
            'font-size': '14px',
            'font-weight': 'bold',
            'margin': '4px',
            'display': 'block',
            'color': '#333'
        });
    element.append(label);

    // Create textarea for the component with inline CSS for better presentation
    const textarea = $('<textarea>')
        .attr('name', component.key)
        .attr('disabled', 'disabled')
        .val(formData[component.key] || '')  // Set the default value from formData
        .css({
            'width': '100%',
            'padding': '10px',
            'font-size': '14px',
            'border': '1px solid #ccc',
            'border-radius': '5px',
            'resize': 'vertical',
            'box-sizing': 'border-box',
            'background-color': '#f9f9f9',
            'color': '#555',
            'margin': '4px'
        });
    element.append(textarea);

    return element;
}



function loadData() {
    // Wires the basedOnContract checkbox change → ReplaceOriginalFile.
    // (Called from form-side custom JS, not from this file.)
    $('input[name="data[basedOnContract]"]').change(function () {
        var isChecked = $(this).is(':checked');
        var value     = $(this).val();
        var requestData = { basedOnContract: isChecked ? value : null };
        console.log(requestData);
        ReplaceOriginalFile();
    });
}


function ReplaceOriginalFile() {
    // this from draft
    documentId = $('#hdDocumentId').val();
    if (typeof documentId === 'undefined' || documentId === null || documentId === '') {

        var documentId = $('#id').val();
        var userId = $('#hdUserId').val();
        var token = window.IdentityAccessToken;

        $.ajax({
            "url": `${CustomCaseApiUrl}/api/UnionCoopCase/ReplaceOriginalFile?docId=${documentId}&userId=${userId}&token=${token}`,
            "method": "POST",
            "contentType": "application/json; charset=utf-8",
            success: function (response) {
                Common.unmask("body-mask");
            },
            error: function () {
                Common.unmask("body-mask");
            }

        });
    }
    else {
        Common.unmask("body-mask");
    }
}


// ===========================================================================
// Inbox custom-column functions
// ===========================================================================

function ApprovedMemoReference(row) {
    var form = GetForm(row);
    return form.approvedMemoReference ?? "";
}

function CandidateName(row) {
    var form = GetForm(row);
    return form.candidateName ?? "";
}

function LPOReference(row) {
    var form = GetForm(row);
    return form.requestReference ?? "";
}


// ---- Shared helper --------------------------------------------------------
// Fetches the case behind a grid row and returns its formData as a parsed
// object. The Portal stores formData as a JSON-string inside a JSON

function GetForm(row) {
      var docId =
        (row && (row.documentId || row.DocumentId ||
                 row.documentID || row.DocumentID ||
                 row.id          || row.Id));
    if (docId === undefined || docId === null || docId === '') return {};

    var token = window.IdentityAccessToken;
    var form  = "";
    var settings = {
        "url"     : "/Document/GetSearchDocument?id=" + encodeURIComponent(docId),
        "method"  : "GET",
        "async"   : false,   // synchronous - the grid expects the return value before next row paints
        "timeout" : 0,
        "headers" : { "Authorization": "Bearer " + token }
    };
    $.ajax(settings).done(function (response) {
        if (!response || response.formData == null) return;
        try {
            var formString = JSON.stringify(response.formData);
            var formParse  = JSON.parse(formString);
            form           = JSON.parse(formParse);
        } catch (e) { form = ""; }
    });
    return form || {};
}


// ===========================================================================
// Inbox custom-column search filter
// ===========================================================================
(function attachInboxCustomColumnFilter() {
    if (window.__inboxCustomColumnFilterInstalled) return;
    window.__inboxCustomColumnFilterInstalled = true;

    // Each entry adds one input to the Search panel + one column-text match
    // on the grid. `label` must match the grid header text exactly (the
    // "Label" you set in Admin -> Nodes -> Inbox -> Custom column).
    var CUSTOM_SEARCH_FIELDS = [
        { id: "txtFilterInboxApprovedMemoReference", label: "Approved Memo Reference", header: "Approved Memo Reference" },
        { id: "txtFilterInboxCandidateName", label: "Candidate Name", header: "Candidate Name" },
        { id: "txtFilterInboxLPOReference", label: "LPO Reference", header: "LPO Reference" }
    ]; 

    // When a custom filter is active, we ask the Portal for this many rows to solve pagination issue in custom search
  
    var BULK_PAGE_SIZE = 10000;

    // URL patterns considered to be node fetches that we should bulk-page.
    // Covers every list node the Portal renders with the same Search panel:
  
    var INBOX_FETCH_URL = /\/(Task|Document)\/(v\d+\/)?List(Inbox|Draft|Completed|Closed|MyRequests|Archived)\b/i;

    function hasActiveCustomSearch() {        
        for (var i = 0; i < CUSTOM_SEARCH_FIELDS.length; i++) {
            if (($('#' + CUSTOM_SEARCH_FIELDS[i].id).val() || '').trim().length > 0) return true;
        }
        return false;
    }

    // --- Loader overlay shown while the bulk-page fetch is in flight -------  
    var bulkInFlight  = 0;
    var bulkMaskName  = "inbox-bulk-mask";

    function showBulkOverlayFallback() {
        if (!document.getElementById('inbox-bulk-loader-css')) {
            var s = document.createElement('style');
            s.id = 'inbox-bulk-loader-css';
            s.textContent =
                '#inbox-bulk-overlay{position:fixed;inset:0;background:rgba(0,0,0,.35);z-index:2147483647;display:flex;align-items:center;justify-content:center;}' +
                '#inbox-bulk-overlay .inbox-bulk-spinner{width:54px;height:54px;border:6px solid rgba(255,255,255,.25);border-top-color:#fff;border-radius:50%;animation:inbox-bulk-spin .9s linear infinite;}' +
                '@keyframes inbox-bulk-spin{to{transform:rotate(360deg)}}';
            (document.head || document.documentElement).appendChild(s);
        }
        if (document.getElementById('inbox-bulk-overlay')) return;
        var ov = document.createElement('div');
        ov.id = 'inbox-bulk-overlay';
        ov.innerHTML = '<div class="inbox-bulk-spinner"></div>';
        document.body.appendChild(ov);
    }
    function hideBulkOverlayFallback() {
        var ov = document.getElementById('inbox-bulk-overlay');
        if (ov && ov.parentNode) ov.parentNode.removeChild(ov);
    }
    function showBulkLoader() {
        if (bulkInFlight++ > 0) return;
        if (typeof Common !== 'undefined' && Common.mask) {
            try { Common.mask(document.body, bulkMaskName); return; } catch (e) {}
        }
        showBulkOverlayFallback();
    }
    function hideBulkLoader() {
        if (--bulkInFlight > 0) return;
        bulkInFlight = 0;
        if (typeof Common !== 'undefined' && Common.unmask) {
            try { Common.unmask(bulkMaskName); } catch (e) {}
        }
        hideBulkOverlayFallback();
    }

    // jQuery ajaxSend hook - rewrites the next list-node fetch to request a
    // bulk page size when a custom filter is active, and shows the loader
    // overlay while it's in flight.
    $(document).ajaxSend(function (event, xhr, options) {
        if (!hasActiveCustomSearch()) return;
        if (!options || !options.url || !INBOX_FETCH_URL.test(options.url)) return;

        // 1) Query-string parameters (GET requests).
        var newUrl = options.url
            .replace(/([?&](?:pageSize|PageSize|page_size|size|length|Length|top|Top|take|Take)=)\d+/gi,
                     '$1' + BULK_PAGE_SIZE)
            .replace(/([?&](?:start|Start|pageIndex|PageIndex|page|Page|skip|Skip|offset|Offset)=)\d+/gi,
                     '$1' + '0');
        if (newUrl !== options.url) options.url = newUrl;

        // 2) JSON body. Walk every key recursively so nested DataTables-style
        //    bodies ({ pagination: { pageSize: 10 } }) also get patched.
        function patchObj(obj) {
            if (!obj || typeof obj !== 'object') return;
            Object.keys(obj).forEach(function (k) {
                var lo = k.toLowerCase();
                if (lo === 'pagesize' || lo === 'page_size' || lo === 'size' ||
                    lo === 'length'   || lo === 'top'       || lo === 'take') {
                    obj[k] = BULK_PAGE_SIZE;
                } else if (lo === 'start'  || lo === 'pageindex' || lo === 'page' ||
                           lo === 'skip'   || lo === 'offset') {
                    obj[k] = 0;
                } else if (typeof obj[k] === 'object') {
                    patchObj(obj[k]);
                }
            });
        }

        if (options.data) {
            var bodyString = typeof options.data === 'string' ? options.data : null;
            var bodyObj    = (bodyString === null) ? options.data : null;

            if (bodyString !== null) {
                try { bodyObj = JSON.parse(bodyString); } catch (e) { bodyObj = null; }
            }

            if (bodyObj && typeof bodyObj === 'object') {
                patchObj(bodyObj);
                options.data = (bodyString === null) ? bodyObj : JSON.stringify(bodyObj);
            } else if (bodyString !== null) {
                options.data = bodyString
                    .replace(/((?:pageSize|PageSize|page_size|size|length|Length|top|Top|take|Take)=)\d+/gi,
                             '$1' + BULK_PAGE_SIZE)
                    .replace(/((?:start|Start|pageIndex|PageIndex|page|Page|skip|Skip|offset|Offset)=)\d+/gi,
                             '$1' + '0');
            }
        }

        // Show the overlay now; hide as soon as THIS specific request resolves.
      
        showBulkLoader();
        var hideOnce = false;
        function safeHide() { if (!hideOnce) { hideOnce = true; hideBulkLoader(); } }
        if (xhr && typeof xhr.always === 'function') {
            xhr.always(safeHide);
        } else {
            // jQuery shim - extremely old builds. Fall back to ajaxComplete
            // filtered by xhr identity (still better than .one()).
            var handler = function (e, ajaxXhr) {
                if (ajaxXhr === xhr) {
                    safeHide();
                    $(document).off('ajaxComplete', handler);
                }
            };
            $(document).on('ajaxComplete', handler);
        }
        // Safety net: if for some reason the xhr never resolves (network
        // wedge, browser stall), force-hide after 40s so the page never
        // stays masked indefinitely.
        setTimeout(safeHide, 40000);
    });

    // Inject inputs into the Portal's Search panel. Re-runs are idempotent.
    function injectInputs() {
        var $row = $('#filtersContainer > .row').first();
        if (!$row.length) return false;

        CUSTOM_SEARCH_FIELDS.forEach(function (f) {
            if ($('#' + f.id).length) return;   // already injected
            var html =
                '<div class="col-lg-3 col-md-3 col-sm-6 customColFilterCol">' +
                  '<div class="form-group">' +
                    '<label class="control-label">' + f.label + '</label>' +
                    '<input type="text" id="' + f.id + '" class="form-control" autocomplete="off" />' +
                  '</div>' +
                '</div>';
            // Append before the action-buttons col so they stay at the end.
            var $actions = $row.find('.list-action-filter').first();
            if ($actions.length) $actions.before(html);
            else                 $row.append(html);
        });
        return true;
    }

    // For each registered field, locate the grid column index by matching
    // the header text, then hide rows whose cell at that index doesn't
    // contain the typed text. Empty input means "no filter from this field".
    function applyClientFilter() {
        $('table').each(function () {
            var $table = $(this);
            var $headerRow = $table.find('thead tr').first();
            if (!$headerRow.length) $headerRow = $table.find('tr').first();
            if (!$headerRow.length) return;

            // Map: column index -> query string for any field that targets it.
            var colQueries = {};
            CUSTOM_SEARCH_FIELDS.forEach(function (f) {
                var q = ($('#' + f.id).val() || '').trim().toLowerCase();
                if (!q) return;
                var idx = -1;
                $headerRow.find('th, td').each(function (i) {
                    var ht = ($(this).text() || '').replace(/\s+/g, ' ').trim();
                    if (ht === f.header) { idx = i; return false; }
                });
                if (idx >= 0) colQueries[idx] = q;
            });

            // No active queries -> show everything (clear any prior hide).
            $table.find('tbody tr, tr').each(function () {
                var $r = $(this);
                if ($r.find('th').length) return;
                var visible = true;
                for (var idxStr in colQueries) {
                    var idx = parseInt(idxStr, 10);
                    var $cell = $r.find('td').eq(idx);
                    var txt = ($cell.text() || '').toLowerCase();
                    if (txt.indexOf(colQueries[idx]) < 0) { visible = false; break; }
                }
                $r.toggle(visible);
            });
        });
    }

    function bindButtons() {
        // Match the Search button across every node: btnFilterInboxSearch,
        // btnFilterDraftSearch, btnFilterCompletedSearch, etc. The Portal
        // names them with a consistent btnFilter<NodeName>Search pattern.
        $('button[id^="btnFilter"][id$="Search"]').each(function () {
            var $btn = $(this);
            if ($btn.data('customColBound')) return;
            $btn.data('customColBound', true);
            $btn.on('click.customColFilter', function () {
                // Server-side search reloads the grid; apply our filter on top
                // after the reload finishes. Use staggered timers because the
                // Portal does the reload asynchronously.
                setTimeout(applyClientFilter, 250);
                setTimeout(applyClientFilter, 750);
                setTimeout(applyClientFilter, 1500);
            });
        });
        $('button[id^="btnFilter"][id$="Clear"]').each(function () {
            var $btn = $(this);
            if ($btn.data('customColBound')) return;
            $btn.data('customColBound', true);
            $btn.on('click.customColFilter', function () {
                CUSTOM_SEARCH_FIELDS.forEach(function (f) { $('#' + f.id).val(''); });
                setTimeout(applyClientFilter, 250);
            });
        });
    }

    function setup() {
        injectInputs();
        bindButtons();
    }

    // Re-run after any ajaxSuccess (the grid reloads on search/sort/page).
    $(document).ajaxSuccess(function () {
        setTimeout(setup, 50);
        // Also re-apply our client filter so it survives grid re-renders.
        setTimeout(applyClientFilter, 300);
    });

    // Initial setup + short poll to catch the first render.
    $(document).ready(function () { setTimeout(setup, 100); });
    var tries = 0;
    (function poll() {
        setup();
        if (tries++ < 40) setTimeout(poll, 250);   // ~10s
    })();
})();
