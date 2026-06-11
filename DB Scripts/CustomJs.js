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
// responses don't leave dropdowns looking broken.
//
// Catches every transport the Portal uses:
//   1. XMLHttpRequest        — Form.io URL dataSrc
//   2. window.fetch          — newer Form.io builds
//   3. jQuery $.ajax         — loadIsHRFromJDE etc. in this file
//   4. Formio.makeRequest    — internal Form.io HTTP client
//
// Display: Portal's Common.mask if loaded, otherwise a self-contained CSS
// overlay. Ref-counted so concurrent calls only show the overlay once.
// URL-matched on "/jderest/orchestrator" so other traffic is unaffected.
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
    // uses the integration. Two markers count as "uses JDE":
    //   - a URL data source pointing at JDEURL / /jderest/orchestrator
    //   - any reference to window.IsHR / window.CurrentUserEmail /
    //     window.EmpDepartmentCode / window.EmpDepartmentDesc /
    //     window.EmpName / window.EmpJobDesc  in customConditional /
    //     customDefaultValue / calculateValue expressions.
    //
    // Detection is automatic — no per-form opt-in required. Forms that
    // don't match (Inbox, Search, other apps' forms) get zero IAM/JDE
    // traffic and the loader never fires. See attachJdeAutoBootstrap().
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
            settings.url.includes("Document/GetDocumentTasks")) {
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
//
// Hits IAM to load only the email of the currently-logged-in user, then
// caches it on window.CurrentUserEmail so later code paths (e.g. JDE
// GetEmployeeInfoByEmail) don't have to refetch.
// Pass an optional callback(email) to chain follow-up calls.
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
//
// Chains: IAM (email) → JDE GetEmployeeInfoByEmail → window.IsHR + window.EmpDepartmentCode.
// Reuses the IAM cache populated by loadCurrentUserFromIAM, so calling both
// at startup costs only one IAM round-trip.
//
// Globals set:
//   window.IsHR                  = boolean (from emp.IsHR)
//   window.EmpDepartmentCode     = string  (from emp.DepartmentCode;        "" when employee not found)
//   window.EmpDepartmentDesc     = string  (from emp.DepartmentDescription; "" when employee not found)
// The description is stored separately so Form.io selects can pre-populate
// their label/template via instance.selectData without waiting for the URL
// data source to finish loading.
//
// Auth header is the base64 of "JDEORCH:ucjde123" — regenerate via:
//   [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("JDEORCH:ucjde123"))
// if you rotate the password.
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
//
// Replaces the old unconditional preload. Watches Form.io for the form(s)
// rendered on this page; when a form's schema references the JDE
// integration — directly via JDEURL / "/jderest/orchestrator", or
// indirectly via the globals the bootstrap populates (window.IsHR,
// window.CurrentUserEmail, window.EmpDepartmentCode, …) — fires the
// chain ONCE and forces every loaded form to redraw so any
// customConditional / customDefaultValue keyed off those globals
// re-evaluates with real values.
//
// Pages that don't render a JDE-using form (Inbox, Search, other apps)
// never trigger the chain → no IAM/JDE round-trip and no loader.
// Forms can still call loadIsHRFromJDE() / loadCurrentUserFromIAM()
// directly if they need the data on demand.
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






