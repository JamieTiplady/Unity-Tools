using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

public class SpriteFromAtlas : MonoBehaviour
{
    [SerializeField] SpriteAtlas atlas;
    [SerializeField] string spriteName;

    void Awake()
    {
        if(!string.IsNullOrEmpty(spriteName))
            GetComponent<Image>().sprite = atlas.GetSprite(spriteName);
    }

    public void LoadSpriteByName(string value)
    {
        if (!string.IsNullOrEmpty(value)) 
        {
            GetComponent<Image>().sprite = atlas.GetSprite(value);
        }
    }
}