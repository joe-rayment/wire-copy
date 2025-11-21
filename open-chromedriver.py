#!/usr/bin/env python3
"""
Simple script to open Chrome with ChromeDriver for manual testing.
Press Ctrl+C to close.
"""

from selenium import webdriver
from selenium.webdriver.chrome.options import Options
import time

print("🌐 Opening Chrome with ChromeDriver...")
print("   Press Ctrl+C to close\n")

options = Options()

# Anti-detection settings (same as your C# app)
options.add_argument('--disable-blink-features=AutomationControlled')
options.add_experimental_option("excludeSwitches", ["enable-automation"])
options.add_experimental_option('useAutomationExtension', False)

# Disable images for faster loading
prefs = {
    "profile.managed_default_content_settings.images": 2
}
options.add_experimental_option("prefs", prefs)

try:
    driver = webdriver.Chrome(options=options)

    # Execute CDP command to hide webdriver
    driver.execute_cdp_cmd('Page.addScriptToEvaluateOnNewDocument', {
        'source': '''
            Object.defineProperty(navigator, 'webdriver', {
                get: () => undefined
            })
        '''
    })

    print("✓ Chrome opened!")
    print("  Navigate to: https://www.nytimes.com")
    print("  Or any other URL you want to test\n")

    # Keep browser open
    while True:
        time.sleep(1)

except KeyboardInterrupt:
    print("\n\n🛑 Closing browser...")
    driver.quit()
    print("✓ Done!")
except Exception as e:
    print(f"\n❌ Error: {e}")
    print("\nMake sure selenium is installed:")
    print("  pip3 install selenium")
