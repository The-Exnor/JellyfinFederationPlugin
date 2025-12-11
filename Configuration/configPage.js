(function() {
    'use strict';

    console.log('[Federation] External script loaded!');

    var pluginId = '12345678-1234-1234-1234-123456789abc';
    var currentConfig = null;
    var pageElement = document.getElementById('federationConfigPage');

    if (!pageElement) {
        console.error('[Federation] Page element not found!');
      return;
    }

    console.log('[Federation] Page element found:', pageElement);

    function escapeHtml(str) {
        if (!str) return '';
  var div = document.createElement('div');
        div.textContent = str;
     return div.innerHTML;
    }

    function generateGuid() {
      return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
   var r = Math.random() * 16 | 0;
            var v = c === 'x' ? r : (r & 0x3 | 0x8);
    return v.toString(16);
  });
    }

    function renderServers() {
 console.log('[Federation] renderServers');
        var container = pageElement.querySelector('#serverListContainer');
        var servers = currentConfig.RemoteServers || [];

        if (servers.length === 0) {
            container.innerHTML = '<p style="opacity:0.7;padding:20px 0;">No servers configured. Click "Add Server" to begin.</p>';
 return;
     }

        var html = '';
servers.forEach(function(server, i) {
            html += '<div class="paperList" style="margin-bottom:20px;padding:20px;background:rgba(255,255,255,0.03);border-radius:6px;">';
         html += '<h4>Server ' + (i + 1) + '</h4>';
   html += '<div class="inputContainer">';
            html += '<label class="inputLabel inputLabelUnfocused">Name</label>';
 html += '<input is="emby-input" type="text" class="txtServerName" data-index="' + i + '" value="' + escapeHtml(server.Name) + '" />';
     html += '</div>';
          html += '<div class="inputContainer">';
    html += '<label class="inputLabel inputLabelUnfocused">URL</label>';
         html += '<input is="emby-input" type="text" class="txtServerUrl" data-index="' + i + '" value="' + escapeHtml(server.Url) + '" placeholder="http://server:8096" />';
    html += '</div>';
    html += '<div class="inputContainer">';
          html += '<label class="inputLabel inputLabelUnfocused">API Key</label>';
     html += '<input is="emby-input" type="password" class="txtServerApiKey" data-index="' + i + '" value="' + escapeHtml(server.ApiKey) + '" />';
       html += '</div>';
    html += '<div class="checkboxContainer checkboxContainer-withDescription" style="margin:15px 0;">';
         html += '<label class="emby-checkbox-label">';
    html += '<input is="emby-checkbox" type="checkbox" class="chkServerEnabled" data-index="' + i + '" ' + (server.Enabled ? 'checked' : '') + ' />';
      html += '<span>Enabled</span>';
      html += '</label>';
        html += '</div>';
     html += '<button is="emby-button" type="button" class="raised button-cancel btnRemoveServer" data-index="' + i + '">Remove</button>';
      html += '</div>';
        });

  container.innerHTML = html;
    }

    function renderMappings() {
        console.log('[Federation] renderMappings');
      var container = pageElement.querySelector('#mappingListContainer');
        var mappings = currentConfig.LibraryMappings || [];

        if (mappings.length === 0) {
       container.innerHTML = '<p style="opacity:0.7;padding:20px 0;">No mappings configured. Click "Add Mapping" to begin.</p>';
      return;
  }

        var html = '';
  mappings.forEach(function(mapping, i) {
            html += '<div class="paperList" style="margin-bottom:20px;padding:20px;background:rgba(255,255,255,0.03);border-radius:6px;">';
          html += '<h4>Mapping ' + (i + 1) + '</h4>';
            html += '<div class="inputContainer">';
        html += '<label class="inputLabel inputLabelUnfocused">Library Name</label>';
            html += '<input is="emby-input" type="text" class="txtMappingName" data-index="' + i + '" value="' + escapeHtml(mapping.LocalLibraryName) + '" placeholder="Federated Movies" />';
            html += '</div>';
   html += '<div class="selectContainer">';
      html += '<label class="selectLabel">Media Type</label>';
   html += '<select is="emby-select" class="selMappingType" data-index="' + i + '">';
            html += '<option value="Movie"' + (mapping.MediaType === 'Movie' ? ' selected' : '') + '>Movies</option>';
       html += '<option value="Series"' + (mapping.MediaType === 'Series' ? ' selected' : '') + '>TV Shows</option>';
   html += '</select>';
    html += '</div>';
            html += '<div class="checkboxContainer checkboxContainer-withDescription" style="margin:15px 0;">';
       html += '<label class="emby-checkbox-label">';
     html += '<input is="emby-checkbox" type="checkbox" class="chkMappingEnabled" data-index="' + i + '" ' + (mapping.Enabled ? 'checked' : '') + ' />';
            html += '<span>Enabled</span>';
       html += '</label>';
     html += '</div>';
      html += '<button is="emby-button" type="button" class="raised button-cancel btnRemoveMapping" data-index="' + i + '">Remove</button>';
            html += '</div>';
        });

        container.innerHTML = html;
    }

    function attachHandlers() {
        console.log('[Federation] attachHandlers');

   var addServerBtn = pageElement.querySelector('.btnAddServer');
        if (addServerBtn) {
            addServerBtn.addEventListener('click', function(e) {
     e.preventDefault();
      console.log('[Federation] ADD SERVER CLICKED');
    currentConfig.RemoteServers = currentConfig.RemoteServers || [];
       currentConfig.RemoteServers.push({
          Id: generateGuid(),
        Name: '',
    Url: '',
          ApiKey: '',
      UserId: '',
           Enabled: true
                });
      renderServers();
       });
  }

        var addMappingBtn = pageElement.querySelector('.btnAddMapping');
        if (addMappingBtn) {
            addMappingBtn.addEventListener('click', function(e) {
        e.preventDefault();
                console.log('[Federation] ADD MAPPING CLICKED');
     currentConfig.LibraryMappings = currentConfig.LibraryMappings || [];
              currentConfig.LibraryMappings.push({
             LocalLibraryName: '',
        MediaType: 'Movie',
          RemoteServerIds: [],
    Enabled: true
         });
     renderMappings();
});
        }

        var form = pageElement.querySelector('.federationConfigForm');
   if (form) {
   form.addEventListener('submit', function(e) {
            e.preventDefault();
        console.log('[Federation] SAVE CLICKED');
     saveConfiguration();
    return false;
          });
        }

    pageElement.addEventListener('click', function(e) {
     var target = e.target.closest('.btnRemoveServer');
            if (target) {
          e.preventDefault();
         var index = parseInt(target.getAttribute('data-index'));
       if (confirm('Remove this server?')) {
         currentConfig.RemoteServers.splice(index, 1);
    renderServers();
       }
        return;
          }

          target = e.target.closest('.btnRemoveMapping');
            if (target) {
              e.preventDefault();
    var index = parseInt(target.getAttribute('data-index'));
  if (confirm('Remove this mapping?')) {
                    currentConfig.LibraryMappings.splice(index, 1);
   renderMappings();
}
         return;
            }
 });

     console.log('[Federation] All handlers attached');
    }

    function saveConfiguration() {
        if (typeof Dashboard === 'undefined') return;
      Dashboard.showLoadingMsg();

     if (currentConfig.RemoteServers) {
            currentConfig.RemoteServers.forEach(function(server, i) {
      var nameEl = pageElement.querySelector('.txtServerName[data-index="' + i + '"]');
           var urlEl = pageElement.querySelector('.txtServerUrl[data-index="' + i + '"]');
  var apiKeyEl = pageElement.querySelector('.txtServerApiKey[data-index="' + i + '"]');
        var enabledEl = pageElement.querySelector('.chkServerEnabled[data-index="' + i + '"]');

       if (nameEl) server.Name = nameEl.value;
          if (urlEl) server.Url = urlEl.value;
           if (apiKeyEl) server.ApiKey = apiKeyEl.value;
  if (enabledEl) server.Enabled = enabledEl.checked;
            });
        }

        if (currentConfig.LibraryMappings) {
       currentConfig.LibraryMappings.forEach(function(mapping, i) {
      var nameEl = pageElement.querySelector('.txtMappingName[data-index="' + i + '"]');
              var typeEl = pageElement.querySelector('.selMappingType[data-index="' + i + '"]');
      var enabledEl = pageElement.querySelector('.chkMappingEnabled[data-index="' + i + '"]');

      if (nameEl) mapping.LocalLibraryName = nameEl.value;
      if (typeEl) mapping.MediaType = typeEl.value;
           if (enabledEl) mapping.Enabled = enabledEl.checked;
    });
        }

   console.log('[Federation] Saving:', currentConfig);

        ApiClient.updatePluginConfiguration(pluginId, currentConfig).then(function() {
 console.log('[Federation] SAVED');
 Dashboard.hideLoadingMsg();
       Dashboard.processPluginConfigurationUpdateResult();
     require(['toast'], function(toast) {
                toast('Configuration saved!');
    });
        }).catch(function(err) {
            console.error('[Federation] Save error:', err);
  Dashboard.hideLoadingMsg();
require(['toast'], function(toast) {
       toast('Save failed');
       });
        });
    }

    function loadConfiguration() {
 console.log('[Federation] loadConfiguration');
        if (typeof ApiClient === 'undefined' || typeof Dashboard === 'undefined') {
          console.error('[Federation] APIs not available, retrying...');
            setTimeout(loadConfiguration, 100);
  return;
     }

        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function(config) {
         console.log('[Federation] Config loaded:', config);
 currentConfig = config || { RemoteServers: [], LibraryMappings: [] };
    renderServers();
   renderMappings();
         attachHandlers();
      Dashboard.hideLoadingMsg();
        }).catch(function(err) {
        console.error('[Federation] Load error:', err);
   Dashboard.hideLoadingMsg();
            if (typeof require === 'function') {
       require(['toast'], function(toast) {
       toast('Failed to load configuration');
       });
            }
        });
    }

    // Start loading
    console.log('[Federation] Starting initialization...');
    loadConfiguration();

})();