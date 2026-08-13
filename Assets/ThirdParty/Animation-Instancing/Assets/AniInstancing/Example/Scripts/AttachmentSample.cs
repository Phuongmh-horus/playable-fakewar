using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using AnimationInstancing;

public class AttachmentSample : MonoBehaviour 
{
	[SerializeField] private AnimationInstancing.AnimationInstancing root;
	public GameObject attachment = null;
	private bool initialize = false;
	public string boneAlias;

	void Update()
	{
		// the attaching should be after the master's initialize, so we put it in the first update.
		if (!initialize)
		{
			initialize = true;
			
			if (root)
            {  
                AnimationInstancing.AnimationInstancing attachmentScript = attachment.GetComponent<AnimationInstancing.AnimationInstancing>();
				root.Attach(boneAlias, attachmentScript);
			}						
		}
	}
}
