let domain = document.location.origin.toLowerCase();
let url = document.URL.toLowerCase();
let CustomCaseApiUrl = "https://caseservicedev.unioncoop.ae";
let caseportalURL = "http://localhost:33333";
let IAMURL = "http://localhost:11111";
let JDEURL = "https://localhost:7042/jderest/orchestrator";

$(document).ready(function () {

    // Preload the currently-logged-in user's profile from IAM so it's
    // available (window.CurrentUserInfo / window.CurrentUserEmail) by the
    // time forms render and need it.
    loadCurrentUserFromIAM();

    // Resolve email → JDE employee record → window.IsHR. Reuses the IAM
    // email above via its internal cache, so only one IAM round-trip total.
    loadIsHRFromJDE();

    // loadTaskhistory in Application metadata
    $(document).ajaxSuccess(function (event, xhr, settings) {
        if (settings.url.includes("Document/GetDocumentByTaskId")) {
            loadTaskhistory();
        }

        if (settings.url.includes("File/ListByDocumentId")) {
            debugger;
            window.addEventListener('contextmenu', (event) => {
                $('li:contains("Replace")').hide();
            });
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

    loadCurrentUserFromIAM(function (email) {
        if (!email) {
            console.warn('loadIsHRFromJDE: no email from IAM — skipping JDE lookup');
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
                window.EmpDepartmentCode = (emp && emp.DepartmentCode)        || '';                
                if (typeof callback === 'function') callback(window.IsHR);
            },
            error: function (xhr, status, error) {
                console.error('JDE GetEmployeeInfoByEmail failed:', status, error, xhr.responseText);
            }
        });
    });
}


function loadTaskhistory() {
    debugger;
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
                debugger;

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



//$(document).ready(function () {

//    debugger;
//    $(document).ajaxSuccess(function (event, xhr, settings) {


//        const heading = document.querySelector("#documentContainerDiv .content-heading");

//        if (
//            heading &&
//            (
//                settings.url.includes("Document/Save") || settings.url.includes("Document/SaveAndSend")
//            ) &&
//            (
//                heading.innerText.includes("Payment Report - Stock Agent") ||
//                heading.innerText.includes("PRICE INCREASE AND DECREASE") ||
//                heading.innerText.includes("NEW ITEMS (Import and Private Label)")
//            )
//           ) {
//            debugger;

//            Common.mask(document.body, "body-mask");

//            setTimeout(function () {
//                ReplaceOriginalFile();
//            }, 5000);

//        }

//    });

//});


function loadData() {

    //to set value
    //$('input[name="data[remedyRequest]"]').val('test')
    $('input[name="data[basedOnContract]"]').change(function () {
        // Check if the checkbox is checked    
        var isChecked = $(this).is(':checked'); var value = $(this).val();
        // Prepare data to send 
        var requestData = {
            basedOnContract: isChecked ? value : null
            // Send the value if checked, null if unchecked      
        };
        //// Call the API using AJAX
        //$.ajax({
        //    url: 'https://api.example.com/data',
        //    // Replace with your API URL
        //    method: 'GET', // or 'POST' depending on your API
        //    data: requestData, // Sending the data
        //    success: function (response) {
        //        // Handle the successful response
        //        $('#result').html(JSON.stringify(response));
        //    }, error: function (xhr, status, error) {
        //        // Handle errors
        //        console.error('Error:', error); $('#result').html('An error occurred. Please try again.');
        //    }
        //});

        console.log(requestData);

        ReplaceOriginalFile();
    });
}


function ReplaceOriginalFile() {
    debugger;
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






