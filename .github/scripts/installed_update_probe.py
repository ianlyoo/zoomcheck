import json, os, pathlib, time, urllib.request
import psutil
from playwright.sync_api import sync_playwright, TimeoutError as PlaywrightTimeout

out=pathlib.Path('update-ui-shots'); out.mkdir(exist_ok=True)
url='http://127.0.0.1:5078'
def backend_pids():
    return sorted(p.pid for p in psutil.process_iter(['name']) if (p.info['name'] or '').lower()=='zoomcheck.backend.exe')
def compact(response):
    try:
        data=response.json()
        return {k:data.get(k) for k in ('currentVersion','latestVersion','availability','downloadState','installerReady','message','started','version','shutdownRequested','title','detail') if k in data}
    except Exception: return {'body':response.text()[:500]}
def status():
    with urllib.request.urlopen(url+'/api/update/status',timeout=5) as response: return json.load(response)
def summary(page,label):
    section=page.locator('#settings-update'); section.scroll_into_view_if_needed()
    info={'label':label,'current':page.locator('#update-current-version').inner_text(),'status':page.locator('#update-status-text').inner_text(),'install_visible':page.locator('#btn-install-update').is_visible(),'install_enabled':page.locator('#btn-install-update').is_enabled()}
    print('UI='+json.dumps(info,ensure_ascii=False),flush=True)
    page.screenshot(path=str(out/(label+'.png')),full_page=True)
    return info

def main():
    initial=backend_pids(); print('INITIAL_BACKEND_PIDS='+json.dumps(initial),flush=True)
    if len(initial)!=1: raise RuntimeError('expected one installed backend')
    profile=pathlib.Path(os.environ['RUNNER_TEMP'])/'zoomcheck-playwright-profile'
    chrome=pathlib.Path(os.environ['PROGRAMFILES'])/'Google/Chrome/Application/chrome.exe'
    with sync_playwright() as playwright:
        context=playwright.chromium.launch_persistent_context(str(profile),executable_path=str(chrome),headless=True,viewport={'width':1440,'height':1000})
        context.add_init_script("localStorage.setItem('zoomcheck.tutorialSeen.v1','1')")
        page=context.pages[0] if context.pages else context.new_page()
        page.goto(url,wait_until='domcontentloaded',timeout=60000)
        page.locator('#btn-open-settings').click()
        summary(page,'01-settings')
        with page.expect_response(lambda r:'/api/update/check' in r.url and r.request.method=='POST',timeout=120000) as first_wait:
            page.locator('#btn-check-update').click()
        first=first_wait.value; first_payload=compact(first)
        print('FIRST_CHECK_HTTP='+str(first.status)+' JSON='+json.dumps(first_payload,ensure_ascii=False),flush=True)
        page.wait_for_timeout(300)
        first_ui=summary(page,'02-first-check')
        page.wait_for_timeout(20000)
        after_ui=summary(page,'03-after-20-seconds')
        with page.expect_response(lambda r:'/api/update/check' in r.url and r.request.method=='POST',timeout=120000) as second_wait:
            page.locator('#btn-check-update').click()
        second=second_wait.value; second_payload=compact(second)
        print('SECOND_CHECK_HTTP='+str(second.status)+' JSON='+json.dumps(second_payload,ensure_ascii=False),flush=True)
        install=page.locator('#btn-install-update')
        try:
            install.wait_for(state='visible',timeout=3000)
            install.click(timeout=3000)
            print('INSTALL_BUTTON_CLICKED=true',flush=True)
            page.screenshot(path=str(out/'04-install-confirmation.png'),full_page=True)
            with page.expect_response(lambda r:'/api/update/install' in r.url and r.request.method=='POST',timeout=30000) as install_wait:
                page.locator('#btn-confirm-apply').click()
            answer=install_wait.value; print('INSTALL_POST_HTTP='+str(answer.status)+' JSON='+json.dumps(compact(answer),ensure_ascii=False),flush=True)
            accepted=answer.status==202
        except PlaywrightTimeout as ex:
            print('INSTALL_BUTTON_OR_REQUEST_UNAVAILABLE='+str(ex).splitlines()[0],flush=True)
            accepted=False
        try: page.screenshot(path=str(out/'05-after-install-click.png'),full_page=True)
        except Exception as ex: print('AFTER_CLICK_SCREENSHOT='+type(ex).__name__,flush=True)
        deadline=time.monotonic()+180 if accepted else time.monotonic()+5
        restarted=False; final=None
        while time.monotonic()<deadline:
            try:
                final=status(); current_pids=backend_pids()
                restarted=bool(current_pids and set(current_pids).isdisjoint(initial))
                if (accepted and restarted) or (not accepted): break
            except Exception: pass
            time.sleep(1)
        print('FINAL_STATUS='+json.dumps(final,ensure_ascii=False),flush=True)
        print('FINAL_BACKEND_PIDS='+json.dumps(backend_pids()),flush=True)
        print('RESTART_OBSERVED='+str(restarted).lower(),flush=True)
        print('OUTCOME='+json.dumps({'first_check':first_payload,'first_ui':first_ui,'after_20s_ui':after_ui,'second_check':second_payload,'install_accepted':accepted,'restart_observed':restarted,'current_version':final.get('currentVersion') if final else None},ensure_ascii=False),flush=True)
        try:
            page.goto(url,wait_until='domcontentloaded',timeout=15000)
            page.screenshot(path=str(out/'06-final-dashboard.png'),full_page=True)
        except Exception as ex: print('FINAL_SCREENSHOT='+type(ex).__name__,flush=True)
        context.close()

if __name__=='__main__': main()
