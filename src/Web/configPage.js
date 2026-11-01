define(['baseView', 'loading', 'emby-input', 'emby-checkbox', 'emby-button', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var SOURCE_TYPES = [
        { value: 'MdbList', name: 'MDBList / IMDb list', hint: 'https://mdblist.com/lists/user/slug, user/slug, or list id' },
        { value: 'XtreamCategory', name: 'xtreamfs category', hint: '' },
        { value: 'LibraryTag', name: 'Library tag', hint: 'Emby tag name' }
    ];

    var SORTS = [
        { value: 'Default', name: 'Leave as Emby has it' },
        { value: 'ReleaseDate', name: 'Release date (oldest first)' },
        { value: 'Name', name: 'Name' }
    ];

    var ART_MODES = [
        { value: 'Auto', name: 'Auto (URL → Kometa → TMDB → generated)' },
        { value: 'Url', name: 'Poster URL' },
        { value: 'Kometa', name: 'Kometa default image' },
        { value: 'TmdbCollection', name: 'TMDB collection' },
        { value: 'Generated', name: 'Generated' },
        { value: 'None', name: 'Leave as is' }
    ];

    function url(path) {
        return ApiClient.getUrl('EmbyCollections/' + path);
    }

    function headers(extra) {
        var h = { 'X-Emby-Token': ApiClient.accessToken() };
        for (var k in extra || {}) {
            h[k] = extra[k];
        }
        return h;
    }

    function getJson(path) {
        return fetch(url(path), { headers: headers(), cache: 'no-store' }).then(function (r) {
            if (!r.ok) {
                throw new Error(path + ': HTTP ' + r.status);
            }
            return r.json();
        });
    }

    function postJson(path, body) {
        return fetch(url(path), {
            method: 'POST',
            headers: headers({ 'Content-Type': 'application/json' }),
            body: body === undefined ? '' : JSON.stringify(body)
        }).then(function (r) {
            if (!r.ok) {
                throw new Error(path + ': HTTP ' + r.status);
            }
            return r.json();
        });
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function options(list, selected) {
        return list.map(function (o) {
            return '<option value="' + esc(o.value) + '"' + (o.value === selected ? ' selected' : '') + '>' + esc(o.name) + '</option>';
        }).join('');
    }

    function newId() {
        return 'xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0;
            return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
        });
    }

    function setPath(obj, path, value) {
        var parts = path.split('.');
        for (var i = 0; i < parts.length - 1; i++) {
            obj = obj[parts[i]];
        }
        obj[parts[parts.length - 1]] = value;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;
        this.config = null;
        this.categories = [];
        this.timer = null;

        var list = view.querySelector('.ec-collections');

        list.addEventListener('input', function (e) { self.onField(e.target, false); });
        list.addEventListener('change', function (e) { self.onField(e.target, true); });
        list.addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-act]');
            if (btn) {
                self.onAction(btn.getAttribute('data-act'), parseInt(btn.getAttribute('data-c'), 10), btn);
            }
        });

        view.querySelector('.btnAdd').addEventListener('click', function () {
            self.collect();
            self.config.Collections.push({
                Id: newId(), Name: 'New collection', Enabled: true, SortName: '', Overview: '', Combine: 'Union',
                Sources: [{ Type: 'MdbList', Value: '', Label: '' }],
                Artwork: { Mode: 'Auto', Url: '', KometaPath: '', TmdbCollectionId: 0, Style: 'Title', LogoQuery: '', ColorTop: '#1f2a44', ColorBottom: '#0b0f19' }
            });
            self.render();
        });
        view.querySelector('.btnSave').addEventListener('click', function () { self.save(); });
        view.querySelector('.btnSyncAll').addEventListener('click', function () {
            self.save().then(function (ok) {
                if (ok) {
                    self.sync('');
                }
            });
        });

        view.querySelector('.ec-tabs').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-tab]');
            if (b) {
                self.showTab(b.getAttribute('data-tab'));
            }
        });
        var all = view.querySelector('[data-panel="all"]');
        all.addEventListener('click', function (e) {
            var tile = e.target.closest('.ec-tile');
            if (tile) {
                self.openEditor(parseInt(tile.getAttribute('data-id'), 10));
                return;
            }
            var btn = e.target.closest('button[data-xact]');
            if (btn) {
                self.editorAction(btn.getAttribute('data-xact'), btn);
            }
        });
        all.addEventListener('change', function (e) {
            if (e.target.classList.contains('ec-xmode') || e.target.classList.contains('ec-xstyle')) {
                self.renderArtFields();
            }
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        loading.show();
        Promise.all([getJson('Settings'), getJson('XtreamCategories').catch(function () { return null; })]).then(function (res) {
            self.config = res[0];
            var x = res[1];
            self.categories = x && x.Categories ? x.Categories : [];
            self.view.querySelector('.ec-xtreaminfo').textContent = x
                ? (x.Found ? 'xtreamfs: ' + self.categories.length + ' categories found.' : 'xtreamfs: no categories.json at ' + x.Path + ' (needs the updated xtreamfs-emby).')
                : '';
            self.fillSettings();
            self.render();
        }).catch(function (err) {
            self.message('Could not load settings: ' + err.message, true);
        }).then(function () {
            loading.hide();
        });
        this.timer = setInterval(function () { self.refreshStatus(); }, 5000);
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        clearInterval(this.timer);
    };

    View.prototype.destroy = function () {
        clearInterval(this.timer);
        BaseView.prototype.destroy.apply(this, arguments);
    };

    View.prototype.fillSettings = function () {
        var c = this.config;
        this.view.querySelectorAll('.ec-section [data-key]').forEach(function (el) {
            var k = el.getAttribute('data-key');
            if (el.type === 'checkbox') {
                el.checked = !!c[k];
            } else {
                el.value = c[k] || '';
            }
        });
    };

    View.prototype.collect = function () {
        var c = this.config;
        this.view.querySelectorAll('.ec-section [data-key]').forEach(function (el) {
            var k = el.getAttribute('data-key');
            c[k] = el.type === 'checkbox' ? el.checked : el.value;
        });
        return c;
    };

    View.prototype.onField = function (el, isChange) {
        var ci = el.getAttribute('data-c');
        var f = el.getAttribute('data-f');
        if (ci === null || !f) {
            return;
        }
        var col = this.config.Collections[parseInt(ci, 10)];
        var value = el.type === 'checkbox' ? el.checked : el.type === 'number' ? (parseInt(el.value, 10) || 0) : el.value;
        setPath(col, f, value);
        if (isChange && (/^Sources\.\d+\.Type$/.test(f) || f === 'Artwork.Mode' || f === 'Artwork.Style')) {
            if (/Type$/.test(f)) {
                setPath(col, f.replace(/Type$/, 'Value'), '');
            }
            this.render();
        }
        if (/^Sources\.\d+\.Value$/.test(f) && el.tagName === 'SELECT') {
            var opt = el.options[el.selectedIndex];
            setPath(col, f.replace(/Value$/, 'Label'), opt ? opt.textContent : '');
        }
        if (f === 'Artwork.Url' || f === 'Artwork.KometaPath') {
            this.updateThumb(parseInt(ci, 10));
        }
    };

    View.prototype.onAction = function (act, ci, btn) {
        var self = this;
        var col = this.config.Collections[ci];
        if (act === 'addsource') {
            col.Sources.push({ Type: 'MdbList', Value: '', Label: '' });
            this.render();
        } else if (act === 'delsource') {
            col.Sources.splice(parseInt(btn.getAttribute('data-s'), 10), 1);
            this.render();
        } else if (act === 'delete') {
            if (window.confirm('Stop managing "' + col.Name + '"? The Emby collection itself is kept.')) {
                this.config.Collections.splice(ci, 1);
                this.render();
            }
        } else if (act === 'sync') {
            this.save().then(function (ok) {
                if (ok) {
                    self.sync(col.Id);
                }
            });
        } else if (act === 'kometa') {
            var box = this.view.querySelector('.ec-kometa[data-c="' + ci + '"]');
            box.innerHTML = '<span class="ec-hint">Searching…</span>';
            getJson('KometaSearch?Query=' + encodeURIComponent(col.Name)).then(function (hits) {
                if (!hits.length) {
                    box.innerHTML = '<span class="ec-hint">No Kometa poster matches "' + esc(col.Name) + '". Try a path like streaming/color/Netflix.jpg.</span>';
                    return;
                }
                box.innerHTML = hits.map(function (h) {
                    return '<button type="button" data-act="pickkometa" data-c="' + ci + '" data-path="' + esc(h.Path) + '" title="' + esc(h.Path) + '"' +
                        (h.Path === col.Artwork.KometaPath ? ' class="sel"' : '') + '><img loading="lazy" src="' + esc(h.Url) + '" alt="" /></button>';
                }).join('');
            }).catch(function (err) {
                box.innerHTML = '<span class="ec-hint err">' + esc(err.message) + '</span>';
            });
        } else if (act === 'pickkometa') {
            col.Artwork.KometaPath = btn.getAttribute('data-path');
            if (col.Artwork.Mode !== 'Auto') {
                col.Artwork.Mode = 'Kometa';
            }
            this.view.querySelectorAll('.ec-kometa[data-c="' + ci + '"] button').forEach(function (b) { b.classList.toggle('sel', b === btn); });
            var input = this.view.querySelector('input[data-c="' + ci + '"][data-f="Artwork.KometaPath"]');
            if (input) {
                input.value = col.Artwork.KometaPath;
            }
            this.updateThumb(ci);
        }
    };

    View.prototype.sourceValueField = function (ci, si, s) {
        var f = 'Sources.' + si + '.Value';
        if (s.Type === 'XtreamCategory') {
            var opts = [{ value: '', name: this.categories.length ? 'Pick a category…' : 'No xtreamfs categories found' }].concat(this.categories.map(function (c) {
                return { value: c.Value, name: (c.Kind === 'movie' ? 'Movies' : 'Series') + ' · ' + c.Name + ' (' + c.Count + ')' };
            }));
            if (s.Value && !this.categories.some(function (c) { return c.Value === s.Value; })) {
                opts.push({ value: s.Value, name: (s.Label || s.Value) + ' (not in index)' });
            }
            return '<select class="ec-select" data-c="' + ci + '" data-f="' + f + '">' + options(opts, s.Value) + '</select>';
        }
        var type = SOURCE_TYPES.filter(function (t) { return t.value === s.Type; })[0] || SOURCE_TYPES[0];
        return '<input class="ec-input" type="text" data-c="' + ci + '" data-f="' + f + '" value="' + esc(s.Value) + '" placeholder="' + esc(type.hint) + '" />';
    };

    View.prototype.render = function () {
        var self = this;
        var html = this.config.Collections.map(function (c, ci) {
            var a = c.Artwork;
            var sources = c.Sources.map(function (s, si) {
                return '<div class="ec-source">' +
                    '<select class="ec-select" data-c="' + ci + '" data-f="Sources.' + si + '.Type">' + options(SOURCE_TYPES, s.Type) + '</select>' +
                    self.sourceValueField(ci, si, s) +
                    '<button is="emby-button" type="button" class="raised ec-small" data-act="delsource" data-c="' + ci + '" data-s="' + si + '"><span>Remove</span></button>' +
                    '</div>';
            }).join('');

            var art = '<div class="ec-field"><span>Artwork</span><select class="ec-select" data-c="' + ci + '" data-f="Artwork.Mode">' + options(ART_MODES, a.Mode) + '</select></div>';
            var show = function (m) { return a.Mode === 'Auto' || a.Mode === m; };
            if (show('Url')) {
                art += '<div class="ec-field"><span>Poster URL</span><input class="ec-input" type="text" data-c="' + ci + '" data-f="Artwork.Url" value="' + esc(a.Url) + '" placeholder="ThePosterDB / MediUX / any image link" /></div>';
            }
            if (show('Kometa')) {
                art += '<div class="ec-field"><span>Kometa image</span><div style="display:flex;gap:.6em"><input class="ec-input" type="text" data-c="' + ci + '" data-f="Artwork.KometaPath" value="' + esc(a.KometaPath) + '" placeholder="Empty: match by collection name" />' +
                    '<button is="emby-button" type="button" class="raised ec-small" data-act="kometa" data-c="' + ci + '"><span>Find</span></button></div></div>' +
                    '<div class="ec-kometa" data-c="' + ci + '"></div>';
            }
            if (show('TmdbCollection')) {
                art += '<div class="ec-field"><span>TMDB collection id</span><input class="ec-input" type="number" min="0" data-c="' + ci + '" data-f="Artwork.TmdbCollectionId" value="' + esc(a.TmdbCollectionId || '') + '" placeholder="e.g. 10 (Star Wars)" /></div>';
            }
            if (show('Generated')) {
                art += '<div class="ec-field"><span>Generated style</span><select class="ec-select" data-c="' + ci + '" data-f="Artwork.Style">' +
                    options([{ value: 'Title', name: 'Title on gradient' }, { value: 'Mosaic', name: 'Mosaic of member posters' }], a.Style) + '</select></div>';
                if (a.Style !== 'Mosaic') {
                    art += '<div class="ec-field"><span>Logo (TMDB)</span><input class="ec-input" type="text" data-c="' + ci + '" data-f="Artwork.LogoQuery" value="' + esc(a.LogoQuery) + '" placeholder="Netflix, A24, network:213, company:41077" /></div>';
                }
                art += '<div class="ec-field"><span>Gradient</span><div style="display:flex;gap:.6em">' +
                    '<input type="color" data-c="' + ci + '" data-f="Artwork.ColorTop" value="' + esc(a.ColorTop || '#1f2a44') + '" />' +
                    '<input type="color" data-c="' + ci + '" data-f="Artwork.ColorBottom" value="' + esc(a.ColorBottom || '#0b0f19') + '" /></div></div>';
            }

            return '<div class="ec-card">' +
                '<div class="ec-cardhead"><input class="ec-input" type="text" data-c="' + ci + '" data-f="Name" value="' + esc(c.Name) + '" />' +
                '<label><input type="checkbox" class="ec-check" data-c="' + ci + '" data-f="Enabled"' + (c.Enabled ? ' checked' : '') + ' />Enabled</label>' +
                '<label title="Never remove titles from this collection, only add"><input type="checkbox" class="ec-check" data-c="' + ci + '" data-f="OnlyAdd"' + (c.OnlyAdd ? ' checked' : '') + ' />Add only</label></div>' +
                '<div class="ec-field"><span>Sort name</span><input class="ec-input" type="text" data-c="' + ci + '" data-f="SortName" value="' + esc(c.SortName) + '" placeholder="Optional" /></div>' +
                '<div class="ec-field"><span>Overview</span><input class="ec-input" type="text" data-c="' + ci + '" data-f="Overview" value="' + esc(c.Overview) + '" placeholder="Optional" /></div>' +
                '<div class="ec-field"><span>Sort order</span><select class="ec-select" data-c="' + ci + '" data-f="Sort">' + options(SORTS, c.Sort || 'Default') + '</select></div>' +
                '<div class="ec-sub">Sources <span class="ec-hint">(combined: anything in any source)</span></div>' + sources +
                '<button is="emby-button" type="button" class="raised ec-small" data-act="addsource" data-c="' + ci + '"><span>Add source</span></button>' +
                '<div class="ec-sub">Artwork</div>' +
                '<div class="ec-art"><div>' + art + '</div><div class="ec-thumb" data-c="' + ci + '"></div></div>' +
                '<div class="ec-status" data-c="' + ci + '">' + self.statusText(c) + '</div>' +
                '<div class="ec-actions">' +
                '<button is="emby-button" type="button" class="raised" data-act="sync" data-c="' + ci + '"><span>Save and sync</span></button>' +
                '<button is="emby-button" type="button" class="raised" data-act="delete" data-c="' + ci + '"><span>Stop managing</span></button>' +
                '</div></div>';
        }).join('');
        this.view.querySelector('.ec-collections').innerHTML = html || '<p class="ec-hint">No collections yet.</p>';
        this.config.Collections.forEach(function (c, ci) { self.updateThumb(ci); });
    };

    View.prototype.updateThumb = function (ci) {
        var c = this.config.Collections[ci];
        var el = this.view.querySelector('.ec-thumb[data-c="' + ci + '"]');
        if (!el || !c) {
            return;
        }
        var a = c.Artwork;
        var src = '';
        if ((a.Mode === 'Url' || a.Mode === 'Auto') && a.Url) {
            src = a.Url;
        } else if ((a.Mode === 'Kometa' || a.Mode === 'Auto') && a.KometaPath) {
            src = 'https://raw.githubusercontent.com/Kometa-Team/Default-Images/master/' + a.KometaPath.split('/').map(encodeURIComponent).join('/');
        } else if (c.BoxSetId) {
            src = ApiClient.getUrl('Items/' + c.BoxSetId + '/Images/Primary', { maxHeight: 450, tag: Date.now() });
        }
        el.style.backgroundImage = src ? 'url("' + src.replace(/"/g, '%22') + '")' : '';
    };

    View.prototype.statusText = function (c) {
        var s = c.LastSync;
        if (!s) {
            return 'Not synced yet.';
        }
        if (s.Error) {
            return '<span class="err">Last sync failed: ' + esc(s.Error) + '</span>';
        }
        var when = new Date(s.Time).toLocaleString();
        var text = 'Last sync ' + esc(when) + ': matched ' + s.Matched + ' of ' + s.SourceItems + ' source items, +' + s.Added + ' / −' + s.Removed + '.';
        if (s.ArtworkSource) {
            text += ' Poster: ' + esc(s.ArtworkSource) + '.';
        }
        (s.Warnings || []).forEach(function (w) {
            text += '<br><span class="err">' + esc(w) + '</span>';
        });
        return text;
    };

    View.prototype.refreshStatus = function () {
        var self = this;
        if (!this.config) {
            return;
        }
        getJson('Status').then(function (st) {
            st.Collections.forEach(function (s) {
                var ci = -1;
                self.config.Collections.forEach(function (c, i) { if (c.Id === s.Id) { ci = i; } });
                if (ci < 0) {
                    return;
                }
                var c = self.config.Collections[ci];
                var changed = JSON.stringify(c.LastSync) !== JSON.stringify(s.LastSync);
                c.LastSync = s.LastSync;
                c.BoxSetId = s.BoxSetId;
                var el = self.view.querySelector('.ec-status[data-c="' + ci + '"]');
                if (el) {
                    el.innerHTML = self.statusText(c);
                }
                if (changed) {
                    self.updateThumb(ci);
                }
            });
            if (st.Running) {
                self.message('Syncing…', false);
            } else if (self.syncing) {
                self.syncing = false;
                self.message('Sync finished.', false);
            }
        }).catch(function () { });
    };

    View.prototype.save = function () {
        var self = this;
        this.collect();
        var bad = this.config.Collections.filter(function (c) { return !c.Name || !c.Name.trim(); });
        if (bad.length) {
            this.message('Every collection needs a name.', true);
            return Promise.resolve(false);
        }
        loading.show();
        return postJson('Settings', this.config).then(function (saved) {
            self.config = saved;
            self.render();
            self.message('Saved.', false);
            return true;
        }).catch(function (err) {
            self.message('Save failed: ' + err.message, true);
            return false;
        }).then(function (ok) {
            loading.hide();
            return ok;
        });
    };

    View.prototype.sync = function (id) {
        var self = this;
        postJson('Sync?Id=' + encodeURIComponent(id || '')).then(function (r) {
            self.syncing = !!r.Started;
            self.message(r.Started ? 'Syncing…' : r.Message, !r.Started);
        }).catch(function (err) {
            self.message('Could not start sync: ' + err.message, true);
        });
    };

    // ---- All collections ------------------------------------------------

    View.prototype.showTab = function (tab) {
        this.view.querySelectorAll('.ec-tabs button').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-tab') === tab); });
        this.view.querySelectorAll('.ec-panel').forEach(function (p) { p.hidden = p.getAttribute('data-panel') !== tab; });
        this.message('');
        if (tab === 'all') {
            this.loadExisting();
        }
    };

    function posterUrl(id, h) {
        return ApiClient.getUrl('Items/' + id + '/Images/Primary', { maxHeight: h || 360, tag: Date.now() });
    }

    View.prototype.loadExisting = function () {
        var self = this;
        var grid = this.view.querySelector('.ec-grid');
        grid.innerHTML = '<span class="ec-hint">Loading…</span>';
        return getJson('Existing').then(function (list) {
            self.existing = list;
            if (!list.length) {
                grid.innerHTML = '<span class="ec-hint">This server has no collections yet.</span>';
                return;
            }
            grid.innerHTML = list.map(function (c) {
                return '<button type="button" class="ec-tile' + (c.Id === self.selectedId ? ' sel' : '') + '" data-id="' + c.Id + '">' +
                    '<div class="ec-thumb" style="' + (c.HasPoster ? 'background-image:url(&quot;' + esc(posterUrl(c.Id, 300)) + '&quot;)' : '') + '"></div>' +
                    '<b title="' + esc(c.Name) + '">' + esc(c.Name) + '</b>' +
                    '<small>' + c.Count + ' title' + (c.Count === 1 ? '' : 's') + (c.ManagedId ? '<span class="ec-badge">Managed</span>' : '') + '</small></button>';
            }).join('');
        }).catch(function (err) {
            grid.innerHTML = '<span class="ec-hint err">' + esc(err.message) + '</span>';
        });
    };

    View.prototype.openEditor = function (id) {
        var self = this;
        var c = (this.existing || []).filter(function (x) { return x.Id === id; })[0];
        if (!c) {
            return;
        }
        this.selectedId = id;
        this.view.querySelectorAll('.ec-tile').forEach(function (t) { t.classList.toggle('sel', parseInt(t.getAttribute('data-id'), 10) === id); });
        var ed = this.view.querySelector('.ec-editor');
        var managedNote = c.ManagedId
            ? '<p class="ec-hint">Managed by this plugin' + (c.ManagedSources ? ' (synced from ' + c.ManagedSources + ' source' + (c.ManagedSources === 1 ? '' : 's') + '). Setting a poster here stops sync from replacing it.' : ' (no sources yet: add some on the Managed tab).') + '</p>'
            : '';
        ed.innerHTML = '<div class="ec-card">' +
            '<div class="ec-art"><div>' +
            '<h2 style="margin:0 0 .4em">' + esc(c.Name) + '</h2>' + managedNote +
            '<div class="ec-field"><span>Name</span><input class="ec-input ec-xname" type="text" value="' + esc(c.Name) + '" /></div>' +
            '<div class="ec-field"><span>Sort name</span><input class="ec-input ec-xsort" type="text" value="' + esc(c.SortName || '') + '" placeholder="Optional" /></div>' +
            '<div class="ec-field"><span>Overview</span><input class="ec-input ec-xoverview" type="text" value="' + esc(c.Overview || '') + '" placeholder="Optional" /></div>' +
            '<div class="ec-field"><span>Sort order</span><select class="ec-select ec-xsortorder">' + options(SORTS.slice(1), c.Sort) + '</select></div>' +
            '<div class="ec-actions"><button is="emby-button" type="button" class="raised button-submit" data-xact="update"><span>Save details</span></button></div>' +
            '</div><div class="ec-thumb ec-xthumb" style="' + (c.HasPoster ? 'background-image:url(&quot;' + esc(posterUrl(c.Id, 450)) + '&quot;)' : '') + '"></div></div>' +

            '<div class="ec-sub">Poster</div>' +
            '<div class="ec-field"><span>New poster from</span><select class="ec-select ec-xmode">' +
            options([{ value: 'Kometa', name: 'Kometa default image' }, { value: 'Url', name: 'Poster URL' }, { value: 'Upload', name: 'Upload a file' },
                { value: 'Generated', name: 'Generated' }, { value: 'TmdbCollection', name: 'TMDB collection' }], 'Kometa') + '</select></div>' +
            '<div class="ec-xartfields"></div>' +
            '<div class="ec-actions"><button is="emby-button" type="button" class="raised button-submit" data-xact="setart"><span>Set poster</span></button>' +
            '<button is="emby-button" type="button" class="raised" data-xact="removeart"><span>Remove poster</span></button></div>' +

            '<div class="ec-sub">Titles</div><div class="ec-members"><span class="ec-hint">Loading…</span></div>' +
            '<div class="ec-actions"><button is="emby-button" type="button" class="raised" data-xact="removeitems"><span>Remove selected titles</span></button></div>' +

            '<div class="ec-sub">Collection</div><div class="ec-actions">' +
            (c.ManagedId ? '' : '<button is="emby-button" type="button" class="raised" data-xact="adopt"><span>Manage with this plugin</span></button>') +
            '<button is="emby-button" type="button" class="raised ec-danger" data-xact="delete"><span>Delete collection</span></button>' +
            '<button is="emby-button" type="button" class="raised" data-xact="close"><span>Close</span></button></div>' +
            '</div>';
        this.renderArtFields();
        getJson('Existing/' + id + '/Items').then(function (items) {
            var box = ed.querySelector('.ec-members');
            box.innerHTML = items.length ? items.map(function (i) {
                return '<label><input type="checkbox" class="ec-check" value="' + i.Id + '" /><span>' + esc(i.Name) + (i.Year ? ' (' + i.Year + ')' : '') + ' <small style="opacity:.6">' + esc(i.Type) + '</small></span></label>';
            }).join('') : '<span class="ec-hint">No titles.</span>';
        });
        ed.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    View.prototype.renderArtFields = function () {
        var ed = this.view.querySelector('.ec-editor');
        var mode = ed.querySelector('.ec-xmode').value;
        var styleEl = ed.querySelector('.ec-xstyle');
        var style = styleEl ? styleEl.value : 'Title';
        var html = '';
        if (mode === 'Kometa') {
            html = '<div class="ec-field"><span>Kometa image</span><div style="display:flex;gap:.6em"><input class="ec-input ec-xkometa" type="text" placeholder="Search or pick below" />' +
                '<button is="emby-button" type="button" class="raised ec-small" data-xact="kometa"><span>Find</span></button></div></div><div class="ec-kometa ec-xkometahits"></div>';
        } else if (mode === 'Url') {
            html = '<div class="ec-field"><span>Poster URL</span><input class="ec-input ec-xurl" type="text" placeholder="ThePosterDB / MediUX / any image link" /></div>';
        } else if (mode === 'Upload') {
            html = '<div class="ec-field"><span>Image file</span><input class="ec-xfile" type="file" accept="image/*" /></div>';
        } else if (mode === 'TmdbCollection') {
            html = '<div class="ec-field"><span>TMDB collection id</span><input class="ec-input ec-xtmdb" type="number" min="1" placeholder="e.g. 10 (Star Wars)" /></div>';
        } else if (mode === 'Generated') {
            html = '<div class="ec-field"><span>Style</span><select class="ec-select ec-xstyle">' +
                options([{ value: 'Title', name: 'Title on gradient' }, { value: 'Mosaic', name: 'Mosaic of member posters' }], style) + '</select></div>' +
                (style === 'Mosaic' ? '' : '<div class="ec-field"><span>Logo (TMDB)</span><input class="ec-input ec-xlogo" type="text" placeholder="Netflix, A24, network:213" /></div>') +
                '<div class="ec-field"><span>Gradient</span><div style="display:flex;gap:.6em"><input type="color" class="ec-xc1" value="#1f2a44" /><input type="color" class="ec-xc2" value="#0b0f19" /></div></div>';
        }
        ed.querySelector('.ec-xartfields').innerHTML = html;
    };

    function readFile(file) {
        return new Promise(function (resolve, reject) {
            var r = new FileReader();
            r.onload = function () { resolve(String(r.result).split(',')[1]); };
            r.onerror = function () { reject(new Error('Could not read the file')); };
            r.readAsDataURL(file);
        });
    }

    View.prototype.editorAction = function (act, btn) {
        var self = this;
        var ed = this.view.querySelector('.ec-editor');
        var id = this.selectedId;
        var c = (this.existing || []).filter(function (x) { return x.Id === id; })[0];
        var val = function (sel) { var el = ed.querySelector(sel); return el ? el.value : ''; };
        var post = function (body, done) {
            loading.show();
            return postJson('Existing/' + id + '/Action', body).then(function (r) {
                if (r && r.Ok === false) {
                    throw new Error(r.Message || 'Failed');
                }
                done(r || {});
            }).catch(function (err) {
                self.message(err.message, true);
            }).then(function () { loading.hide(); });
        };

        if (act === 'close') {
            ed.innerHTML = '';
            this.selectedId = null;
            this.view.querySelectorAll('.ec-tile').forEach(function (t) { t.classList.remove('sel'); });
        } else if (act === 'update') {
            post({ Action: 'Update', Name: val('.ec-xname'), SortName: val('.ec-xsort'), Overview: val('.ec-xoverview'), Sort: val('.ec-xsortorder') }, function () {
                self.message('Saved.');
                self.afterManageChange(c.ManagedId);
            });
        } else if (act === 'kometa') {
            var q = val('.ec-xkometa') || c.Name;
            var hitsBox = ed.querySelector('.ec-xkometahits');
            hitsBox.innerHTML = '<span class="ec-hint">Searching…</span>';
            getJson('KometaSearch?Query=' + encodeURIComponent(q)).then(function (hits) {
                hitsBox.innerHTML = hits.length ? hits.map(function (h) {
                    return '<button type="button" data-xact="pick" data-path="' + esc(h.Path) + '" title="' + esc(h.Path) + '"><img loading="lazy" src="' + esc(h.Url) + '" alt="" /></button>';
                }).join('') : '<span class="ec-hint">No Kometa poster matches "' + esc(q) + '".</span>';
            }).catch(function (err) { hitsBox.innerHTML = '<span class="ec-hint err">' + esc(err.message) + '</span>'; });
        } else if (act === 'pick') {
            ed.querySelector('.ec-xkometa').value = btn.getAttribute('data-path');
            ed.querySelectorAll('.ec-xkometahits button').forEach(function (b) { b.classList.toggle('sel', b === btn); });
        } else if (act === 'setart') {
            var mode = val('.ec-xmode');
            var body = { Action: 'SetArtwork', ArtworkMode: mode, Artwork: {} };
            var go = function () {
                post(body, function (r) {
                    self.message('Poster set' + (r.Source ? ' (' + r.Source + ')' : '') + '.');
                    ed.querySelector('.ec-xthumb').style.backgroundImage = 'url("' + posterUrl(id, 450) + '")';
                    self.afterManageChange(c.ManagedId, true);
                });
            };
            if (mode === 'Kometa') {
                var k = val('.ec-xkometa');
                if (!k || k.indexOf('/') < 0) {
                    self.message('Pick a Kometa poster first (Find, then click one).', true);
                    return;
                }
                body.Artwork.KometaPath = k;
            } else if (mode === 'Url') {
                body.Artwork.Url = val('.ec-xurl');
            } else if (mode === 'TmdbCollection') {
                body.Artwork.TmdbCollectionId = parseInt(val('.ec-xtmdb'), 10) || 0;
            } else if (mode === 'Generated') {
                body.Artwork.Style = val('.ec-xstyle') || 'Title';
                body.Artwork.LogoQuery = val('.ec-xlogo');
                body.Artwork.ColorTop = val('.ec-xc1');
                body.Artwork.ColorBottom = val('.ec-xc2');
            } else if (mode === 'Upload') {
                var f = ed.querySelector('.ec-xfile').files[0];
                if (!f) {
                    self.message('Choose an image file first.', true);
                    return;
                }
                readFile(f).then(function (data) {
                    body.Data = data;
                    body.MimeType = f.type || 'image/jpeg';
                    go();
                }).catch(function (err) { self.message(err.message, true); });
                return;
            }
            go();
        } else if (act === 'removeart') {
            post({ Action: 'RemoveArtwork' }, function () {
                self.message('Poster removed.');
                ed.querySelector('.ec-xthumb').style.backgroundImage = '';
                self.afterManageChange(c.ManagedId, true);
            });
        } else if (act === 'removeitems') {
            var ids = Array.prototype.map.call(ed.querySelectorAll('.ec-members input:checked'), function (i) { return parseInt(i.value, 10); });
            if (!ids.length) {
                self.message('Tick the titles to remove first.', true);
                return;
            }
            post({ Action: 'RemoveItems', ItemIds: ids }, function (r) {
                self.message('Removed ' + ids.length + ' title' + (ids.length === 1 ? '' : 's') + '.' + (r.Note ? ' ' + r.Note : ''));
                self.afterManageChange(null, true);
            });
        } else if (act === 'adopt') {
            post({ Action: 'Adopt' }, function () {
                self.message('"' + c.Name + '" is now managed. It is add-only, so nothing is removed; add sources on the Managed tab.');
                self.afterManageChange(true, true);
            });
        } else if (act === 'delete') {
            if (!window.confirm('Delete the collection "' + c.Name + '"? The titles in it are not touched.')) {
                return;
            }
            post({ Action: 'Delete' }, function () {
                self.message('Deleted "' + c.Name + '".');
                ed.innerHTML = '';
                self.selectedId = null;
                self.afterManageChange(c.ManagedId);
            });
        }
    };

    /// Reload what changed: the grid, the open editor, and the Managed tab when a managed collection changed.
    View.prototype.afterManageChange = function (managedChanged, reopen) {
        var self = this;
        this.loadExisting().then(function () {
            if (reopen && self.selectedId) {
                var keepMsg = self.view.querySelector('.ec-msg').textContent;
                self.openEditor(self.selectedId);
                self.message(keepMsg);
            }
        });
        if (managedChanged) {
            getJson('Settings').then(function (cfg) {
                self.config = cfg;
                self.fillSettings();
                self.render();
            });
        }
    };

    View.prototype.message = function (text, isError) {
        var el = this.view.querySelector('.ec-msg');
        el.textContent = text || '';
        el.classList.toggle('err', !!isError);
    };

    return View;
});
