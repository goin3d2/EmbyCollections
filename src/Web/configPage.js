define(['baseView', 'loading', 'emby-button', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var SOURCE_TYPES = [
        { value: 'MdbList', name: 'MDBList / IMDb list', hint: 'https://mdblist.com/lists/user/slug, user/slug, or list id' },
        { value: 'XtreamCategory', name: 'xtreamfs category', hint: '' },
        { value: 'LibraryTag', name: 'Library tag', hint: 'Emby tag name' }
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
                '<label><input type="checkbox" data-c="' + ci + '" data-f="Enabled"' + (c.Enabled ? ' checked' : '') + ' />Enabled</label></div>' +
                '<div class="ec-field"><span>Sort name</span><input class="ec-input" type="text" data-c="' + ci + '" data-f="SortName" value="' + esc(c.SortName) + '" placeholder="Optional" /></div>' +
                '<div class="ec-field"><span>Overview</span><input class="ec-input" type="text" data-c="' + ci + '" data-f="Overview" value="' + esc(c.Overview) + '" placeholder="Optional" /></div>' +
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

    View.prototype.message = function (text, isError) {
        var el = this.view.querySelector('.ec-msg');
        el.textContent = text || '';
        el.classList.toggle('err', !!isError);
    };

    return View;
});
