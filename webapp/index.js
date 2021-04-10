function appOnload() {
    $.getJSON("update.json", updateCallback);
    //$.getJSON("https://atcsmon1.z20.web.core.windows.net/update.json", updateCallback);
}

function updateCallback(data) {
    if (!data.entries || data.entries.count == 0) return;

    var el = $("#mainAccordion");
    el.empty();

    data.entries.forEach(function (item, idx) {
        // Random id
        var id = "aItem-" + Math.round(Math.random() * 1000000).toString();
        var collapseId = "collapseAItem-" + Math.round(Math.random() * 1000000).toString();
        
        // For each item, we add a new accordion row with content
        var aItem = $("<div class='accordion-item'/>");
        aItem.append("<h2 class='accordion-header' id='" + id + "'><button class='accordion-button collapsed' type='button' data-bs-toggle='collapse' data-bs-target='#" + collapseId + "' aria-expanded='true' aria-controls='" + collapseId + "'>" + item.name + "</button></h2>");
        el.append(aItem);

        // Add the content
        aItem = $("<div id='" + collapseId + "' class='accordion-collapse collapse' aria-labelledby='" + id + "' data-bs-parent='#mainAccordion'><div class='accordion-body'><span style='display:inline-block'><img src='" + item.sas + "' data-original-uri='" + item.sas + "' class='img-fluid' alt='ATCS Screenshot' /></span></div></div>");
        el.append(aItem);

        $("div#" + collapseId + " span").zoom({
            'on': 'off'  // Toggle prevents it from disable itself when we stop clicking/pressing/touching
        });

        // And auto-enable
        $("div#" + collapseId + " span").trigger('click.zoom');

        console.log(item);
    });

    // Enable refreshing images
    setInterval(function () { refreshImages(); }, 5000);
}

function refreshImages() {
    $("img[data-original-uri]").each(function () {
        var rnd = Math.round(Math.random() * 1000000).toString();;
        var url = $(this).data("original-uri");

        if (url.indexOf("?") >= 0)
            url += "&";
        else
            url += "?";
        
        // Add the random number to the URI
        url += rnd;
        
        // Change the URI
        $(this).attr('src', url);
    });

    console.log("Refreshed at " + Date.now);
}
