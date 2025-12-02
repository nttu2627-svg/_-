import json
import os

path = r"c:\Users\ls990\Downloads\_-\ai_china_town-master\src\data\schedules.json"
try:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()

    new_content = content.replace("Apartment_F1", "Apartment")
    new_content = new_content.replace("Apartment_F2", "Apartment")

    with open(path, 'w', encoding='utf-8') as f:
        f.write(new_content)
    print("Successfully updated schedules.json")
except Exception as e:
    print(f"Error: {e}")
